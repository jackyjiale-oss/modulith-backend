using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Security;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Phone;
using TemplateName.Modules.Auth.Application.Phone.RequestCode;
using TemplateName.Modules.Auth.Application.StepUp;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.Modules.Auth.Domain.Verification.Events;

namespace TemplateName.UnitTests.Auth.Phone;

public sealed class RequestPhoneCodeCommandHandlerTests
{
    private const string Number = "+60123456789";

    private static readonly DateTimeOffset Now = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly IPhoneVerificationCodeRepository _codes = Substitute.For<IPhoneVerificationCodeRepository>();
    private readonly IMfaMethodRepository _mfaMethods = Substitute.For<IMfaMethodRepository>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly ISecretProtector _secretProtector = Substitute.For<ISecretProtector>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<PhoneVerificationCode> _issued = [];
    private readonly User _user = User.Register("alice@example.com", "Alice", "en", "hash", Now.AddDays(-1)).Value;
    private readonly PhoneOptions _options = new();
    private readonly RequestPhoneCodeCommandHandler _sut;

    public RequestPhoneCodeCommandHandlerTests()
    {
        var session = StartSession(_user);
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
        _sessions.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);
        _currentUser.UserId.Returns(_user.Id);
        _currentUser.SessionId.Returns(session.Id);
        _currentUser.AuthTime.Returns(Now.AddHours(-1));
        _currentUser.AuthMethods.Returns(["pwd"]);
        _codes.ListPendingAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([]);
        _codes.ListIssuedSinceAsync(_user.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([]);
        _codes.Add(Arg.Do<PhoneVerificationCode>(_issued.Add));
        _secretProtector.Protect(Arg.Any<string>()).Returns(call => "protected:" + call.Arg<string>());
        var time = new FakeTimeProvider(Now);
        _sut = new RequestPhoneCodeCommandHandler(
            _users,
            _sessions,
            _codes,
            _mfaMethods,
            new StepUpPolicy(_currentUser, Options.Create(new StepUpOptions()), time),
            _currentUser,
            _secretProtector,
            _unitOfWork,
            Options.Create(_options),
            time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Issues_a_code_and_publishes_only_the_encrypted_code()
    {
        var result = await _sut.HandleAsync(new RequestPhoneCodeCommand("+60 12-345 6789"), Ct);

        result.IsSuccess.ShouldBeTrue();
        var code = _issued.ShouldHaveSingleItem();
        code.PhoneNumber.ShouldBe(Number);
        result.Value.ExpiresAt.ShouldBe(Now.AddMinutes(10));
        code.ExpiresAt.ShouldBe(Now.AddMinutes(10));
        var requested = code.DomainEvents.OfType<PhoneVerificationRequestedDomainEvent>().ShouldHaveSingleItem();
        requested.ProtectedCode.ShouldStartWith("protected:");
        var plain = requested.ProtectedCode["protected:".Length..];
        plain.ShouldMatch(@"^[0-9]{6}\z");
        code.Matches(plain).ShouldBeTrue();
        requested.PhoneNumber.ShouldBe(Number);
        requested.ExpiresAt.ShouldBe(code.ExpiresAt);
        requested.ToString().ShouldNotContain(plain);
        requested.ToString().ShouldNotContain("60123456789");
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Issuing_a_code_writes_the_user_row_so_parallel_requests_conflict()
    {
        var result = await _sut.HandleAsync(new RequestPhoneCodeCommand(Number), Ct);

        // The user's RowVersion guards the save: of two simultaneous requests only one can write the row, so the limits cannot be raced.
        result.IsSuccess.ShouldBeTrue();
        _user.PhoneCodeRequestedAt.ShouldBe(Now);
    }

    [Fact]
    public async Task A_refused_request_does_not_touch_the_user()
    {
        _codes.GetLastIssuedAtAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(Now.AddSeconds(-20));

        await _sut.HandleAsync(new RequestPhoneCodeCommand(Number), Ct);

        _user.PhoneCodeRequestedAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_new_code_ends_the_previous_one()
    {
        var previous = PhoneVerificationCode.Issue(_user.Id, Number, "111111", "protected", TimeSpan.FromMinutes(10), Now.AddMinutes(-5));
        _codes.GetLastIssuedAtAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(Now.AddMinutes(-5));
        _codes.ListPendingAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([previous]);

        var result = await _sut.HandleAsync(new RequestPhoneCodeCommand(Number), Ct);

        result.IsSuccess.ShouldBeTrue();
        previous.InvalidatedAt.ShouldBe(Now);
        _issued.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_cooldown_refuses_with_the_retry_time()
    {
        _codes.GetLastIssuedAtAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(Now.AddSeconds(-20));

        var result = await _sut.HandleAsync(new RequestPhoneCodeCommand(Number), Ct);

        var error = result.Error.ShouldBeOfType<TooManyCodesError>();
        error.Code.ShouldBe("rate_limit.exceeded");
        error.RetryAfter.ShouldBe(TimeSpan.FromSeconds(40));
        _issued.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task The_daily_cap_refuses_until_the_oldest_code_leaves_the_window()
    {
        _codes.GetLastIssuedAtAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(Now.AddHours(-1));
        _codes.ListIssuedSinceAsync(_user.Id, Now.AddDays(-1), Arg.Any<CancellationToken>())
            .Returns([Now.AddHours(-20), Now.AddHours(-10), Now.AddHours(-5), Now.AddHours(-2), Now.AddHours(-1)]);

        var result = await _sut.HandleAsync(new RequestPhoneCodeCommand(Number), Ct);

        result.Error.ShouldBeOfType<TooManyCodesError>().RetryAfter.ShouldBe(TimeSpan.FromHours(4));
        _issued.ShouldBeEmpty();
    }

    [Fact]
    public async Task Four_codes_in_a_day_leave_room_for_a_fifth()
    {
        _codes.ListIssuedSinceAsync(_user.Id, Now.AddDays(-1), Arg.Any<CancellationToken>())
            .Returns([Now.AddHours(-20), Now.AddHours(-10), Now.AddHours(-5), Now.AddHours(-2)]);

        (await _sut.HandleAsync(new RequestPhoneCodeCommand(Number), Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Replacing_a_verified_number_needs_step_up()
    {
        _user.VerifyPhoneNumber("+60198765432", Now.AddDays(-1));
        _currentUser.AuthTime.Returns(Now.AddHours(-1));

        var result = await _sut.HandleAsync(new RequestPhoneCodeCommand(Number), Ct);

        result.Error.ShouldBe(MfaErrors.StepUpRequired);
        _issued.ShouldBeEmpty();
    }

    [Fact]
    public async Task Replacing_a_verified_number_with_a_fresh_step_up_issues_a_code()
    {
        _user.VerifyPhoneNumber("+60198765432", Now.AddDays(-1));
        _currentUser.AuthTime.Returns(Now.AddMinutes(-1));

        (await _sut.HandleAsync(new RequestPhoneCodeCommand(Number), Ct)).IsSuccess.ShouldBeTrue();
        _user.PhoneNumber.ShouldBe("+60198765432");
    }

    [Fact]
    public async Task Adding_the_first_number_needs_no_step_up()
    {
        _currentUser.AuthTime.Returns((DateTimeOffset?)null);

        (await _sut.HandleAsync(new RequestPhoneCodeCommand(Number), Ct)).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("0123456789")]
    [InlineData("+60321234567")]
    [InlineData("+abc")]
    public async Task An_invalid_number_is_refused(string number)
    {
        var result = await _sut.HandleAsync(new RequestPhoneCodeCommand(number), Ct);

        result.Error.ShouldBe(PhoneErrors.InvalidPhoneNumber);
        _issued.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_region_outside_the_allowlist_is_refused()
    {
        _options.AllowedRegions = ["SG"];

        (await _sut.HandleAsync(new RequestPhoneCodeCommand(Number), Ct)).Error.ShouldBe(PhoneErrors.InvalidPhoneNumber);
    }

    [Fact]
    public async Task A_caller_without_a_live_session_is_not_found()
    {
        _currentUser.SessionId.Returns((Guid?)null);

        (await _sut.HandleAsync(new RequestPhoneCodeCommand(Number), Ct)).Error.Type.ShouldBe(TemplateName.SharedKernel.ErrorType.NotFound);
    }

    [Fact]
    public void The_command_never_prints_the_number() =>
        new RequestPhoneCodeCommand(Number).ToString().ShouldNotContain("60123456789");

    private static UserSession StartSession(User user)
    {
        var (session, _) = UserSession.Start(
            user.Id,
            AuthenticationMethods.Password,
            "Phone",
            userAgent: null,
            ipAddress: null,
            user.SecurityStamp,
            [.. Enumerable.Range(1, 32).Select(value => (byte)value)],
            TimeSpan.FromDays(14),
            TimeSpan.FromDays(90),
            Now.AddHours(-1));
        return session;
    }
}
