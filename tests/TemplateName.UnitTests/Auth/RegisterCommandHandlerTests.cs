using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Passwords;
using TemplateName.Modules.Auth.Application.Registration.Register;
using TemplateName.Modules.Auth.Application.Verification;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.Modules.Auth.Domain.Verification.Events;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth;

public sealed class RegisterCommandHandlerTests
{
    private const string Password = "correct horse battery";
    private const string PasswordHash = "hashed-password";
    private const string TokenValue = "plain-token-value";

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly byte[] TokenHash = [.. Enumerable.Range(1, 32).Select(value => (byte)value)];

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IRoleRepository _roles = Substitute.For<IRoleRepository>();
    private readonly IVerificationCodeRepository _verificationCodes = Substitute.For<IVerificationCodeRepository>();
    private readonly IPasswordHasher _passwordHasher = Substitute.For<IPasswordHasher>();
    private readonly IBreachedPasswordChecker _breachedPasswordChecker = Substitute.For<IBreachedPasswordChecker>();
    private readonly ISecureTokenService _tokenService = Substitute.For<ISecureTokenService>();
    private readonly ISecretProtector _secretProtector = Substitute.For<ISecretProtector>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IClientContext _clientContext = Substitute.For<IClientContext>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly Role _userRole = Role.CreateSystem(SystemRoles.User, "Every registered user", Now);
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly RegisterCommandHandler _sut;

    public RegisterCommandHandlerTests()
    {
        _passwordHasher.Hash(Arg.Any<string>()).Returns(PasswordHash);
        _tokenService.Generate().Returns(new GeneratedToken(TokenValue, TokenHash));
        _secretProtector.Protect(Arg.Any<string>()).Returns(call => "protected:" + call.Arg<string>());
        _roles.GetByNormalizedNameAsync(Role.NormalizeName(SystemRoles.User), Arg.Any<CancellationToken>()).Returns(_userRole);
        _clientContext.IpAddress.Returns("203.0.113.7");
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));

        _sut = new RegisterCommandHandler(
            _users,
            _roles,
            _verificationCodes,
            _passwordHasher,
            _breachedPasswordChecker,
            _tokenService,
            _secretProtector,
            _auditWriter,
            _clientContext,
            _unitOfWork,
            Options.Create(new VerificationOptions()),
            new FakeTimeProvider(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(129)]
    [InlineData(1024 * 1024)]
    public async Task Oversized_password_is_rejected_before_hashing(int length)
    {
        // The pipeline the host builds: the validation decorator runs the validator before the handler.
        var pipeline = new ValidationDecorator.CommandHandler<RegisterCommand>(
            _sut,
            [new RegisterCommandValidator(Options.Create(new PasswordOptions()))]);

        var result = await pipeline.HandleAsync(Command() with { Password = new string('p', length) }, Ct);

        result.Error.ShouldBeOfType<ValidationError>().Errors.Keys.ShouldContain("password");
        _passwordHasher.DidNotReceiveWithAnyArgs().Hash(default!);
        await _breachedPasswordChecker.DidNotReceiveWithAnyArgs().IsBreachedAsync(default!, Ct);
        await _users.DidNotReceiveWithAnyArgs().GetByNormalizedEmailAsync(default!, Ct);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Breached_password_returns_auth_password_breached()
    {
        _breachedPasswordChecker.IsBreachedAsync(Password, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(UserErrors.PasswordBreached);
        result.Error.Code.ShouldBe("auth.password_breached");
        result.Error.Type.ShouldBe(ErrorType.Validation);
        await _users.DidNotReceiveWithAnyArgs().GetByNormalizedEmailAsync(default!, Ct);
        _users.DidNotReceiveWithAnyArgs().Add(default!);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Existing_email_notes_attempt_and_does_not_create_a_user()
    {
        var existing = User.Register("Alice@Example.com", "Alice", "ms", "old-hash", Now.AddDays(-1)).Value;
        existing.ClearDomainEvents();
        _users.GetByNormalizedEmailAsync("ALICE@EXAMPLE.COM", Arg.Any<CancellationToken>()).Returns(existing);

        var result = await _sut.HandleAsync(Command() with { Email = " alice@EXAMPLE.com " }, Ct);

        result.IsSuccess.ShouldBeTrue();
        existing.DomainEvents.ShouldHaveSingleItem().ShouldBe(new RegistrationAttemptedDomainEvent(existing.Id, "Alice@Example.com", "ms"));
        existing.PasswordHash.ShouldBe("old-hash");
        _users.DidNotReceiveWithAnyArgs().Add(default!);
        _verificationCodes.DidNotReceiveWithAnyArgs().Add(default!);
        _tokenService.DidNotReceive().Generate();
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.RegisterDuplicate);
        audit.Succeeded.ShouldBeFalse();
        audit.UserId.ShouldBe(existing.Id);
        audit.AttemptedIdentifier.ShouldBe("a****@EXAMPLE.com");
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);

        // The hash is paid before the lookup, so the duplicate path costs the same as a new account.
        Received.InOrder(() =>
        {
            _passwordHasher.Hash(Password);
            _users.GetByNormalizedEmailAsync("ALICE@EXAMPLE.COM", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task New_user_gets_user_role_code_and_audit_in_one_save()
    {
        User? added = null;
        VerificationCode? code = null;
        _users.Add(Arg.Do<User>(user => added = user));
        _verificationCodes.Add(Arg.Do<VerificationCode>(issued => code = issued));

        var result = await _sut.HandleAsync(Command() with { DisplayName = "  Alice  ", Locale = "ms" }, Ct);

        result.IsSuccess.ShouldBeTrue();
        added.ShouldNotBeNull();
        added.Email.ShouldBe("alice@example.com");
        added.DisplayName.ShouldBe("Alice");
        added.Locale.ShouldBe("ms");
        added.PasswordHash.ShouldBe(PasswordHash);
        added.EmailConfirmed.ShouldBeFalse();
        added.Roles.ShouldHaveSingleItem().RoleId.ShouldBe(_userRole.Id);
        added.DomainEvents.ShouldContain(new UserRegisteredDomainEvent(added.Id, "alice@example.com", "ms"));

        code.ShouldNotBeNull();
        code.UserId.ShouldBe(added.Id);
        code.Purpose.ShouldBe(VerificationPurpose.EmailVerify);
        code.Target.ShouldBe("ALICE@EXAMPLE.COM");
        code.TokenHash.ShouldBe(TokenHash);
        code.CreatedAt.ShouldBe(Now);
        code.ExpiresAt.ShouldBe(Now.AddHours(1));
        code.CreatedIp.ShouldBe("203.0.113.7");
        var issued = code.DomainEvents.OfType<VerificationCodeIssuedDomainEvent>().ShouldHaveSingleItem();
        issued.ProtectedToken.ShouldBe("protected:" + TokenValue);
        issued.Purpose.ShouldBe(VerificationPurpose.EmailVerify);

        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.Registered);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(added.Id);
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);

        Received.InOrder(() =>
        {
            _breachedPasswordChecker.IsBreachedAsync(Password, Arg.Any<CancellationToken>());
            _passwordHasher.Hash(Password);
            _users.GetByNormalizedEmailAsync("ALICE@EXAMPLE.COM", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Locale_is_stored_in_its_canonical_form()
    {
        User? added = null;
        _users.Add(Arg.Do<User>(user => added = user));

        await _sut.HandleAsync(Command() with { Locale = "ZH-hans" }, Ct);

        added.ShouldNotBeNull().Locale.ShouldBe("zh-Hans");
    }

    [Fact]
    public async Task Missing_user_role_fails_loudly_instead_of_registering_a_user_without_a_role()
    {
        _roles.GetByNormalizedNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Role?)null);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => _sut.HandleAsync(Command(), Ct));

        exception.Message.ShouldContain(SystemRoles.User);
        _users.DidNotReceiveWithAnyArgs().Add(default!);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public void Command_text_never_shows_the_password()
    {
        Command().ToString().ShouldNotContain(Password);
    }

    private static RegisterCommand Command() => new("alice@example.com", Password, "Alice", "en");
}
