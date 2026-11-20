using TemplateName.Modules.Auth.Application.Me;
using TemplateName.Application.Common.Concurrency;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Me.GetMe;
using TemplateName.Modules.Auth.Application.Me.Update;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth;

public sealed class UpdateProfileCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IQueryHandler<GetMeQuery, MeResponse> _getMe = Substitute.For<IQueryHandler<GetMeQuery, MeResponse>>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly User _user;
    private readonly UpdateProfileCommandHandler _sut;

    public UpdateProfileCommandHandlerTests()
    {
        _user = User.Register("alice@example.com", "Alice", "en", "hash", Now.AddDays(-1)).Value;
        _user.ConfirmEmail(Now.AddDays(-1));
        _currentUser.UserId.Returns(_user.Id);
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));
        _sut = new UpdateProfileCommandHandler(_users, _currentUser, _getMe, _auditWriter, _unitOfWork, new FakeTimeProvider(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Update_changes_the_profile_stores_the_canonical_locale_and_returns_the_current_user()
    {
        var me = new MeResponse(_user.Id, "alice@example.com", true, "Alicia", "ms-MY", "Asia/Kuala_Lumpur", ["User"], ["a.b.c"]);
        _getMe.HandleAsync(Arg.Any<GetMeQuery>(), Arg.Any<CancellationToken>()).Returns(me);

        var result = await _sut.HandleAsync(new UpdateProfileCommand(" Alicia ", "MS-my", "Asia/Kuala_Lumpur"), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(me);
        _user.DisplayName.ShouldBe("Alicia");
        _user.Locale.ShouldBe("ms-MY");
        _user.TimeZone.ShouldBe("Asia/Kuala_Lumpur");
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.ProfileUpdated);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(_user.Id);
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Unknown_user_is_not_found_and_nothing_is_saved()
    {
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _sut.HandleAsync(new UpdateProfileCommand("Alicia", "ms", "UTC"), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(UserErrors.NotFound(_user.Id).Code);
        _auditEntries.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Suspended_user_is_not_found_and_keeps_the_old_profile()
    {
        _user.Suspend(Now);

        var result = await _sut.HandleAsync(new UpdateProfileCommand("Alicia", "ms", "UTC"), Ct);

        result.IsFailure.ShouldBeTrue();
        _user.DisplayName.ShouldBe("Alice");
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Anonymous_caller_is_not_found()
    {
        _currentUser.UserId.Returns((Guid?)null);

        var result = await _sut.HandleAsync(new UpdateProfileCommand("Alicia", "ms", "UTC"), Ct);

        result.IsFailure.ShouldBeTrue();
        await _users.DidNotReceiveWithAnyArgs().GetByIdAsync(default, Ct);
    }

    [Fact]
    public async Task A_stale_if_match_is_precondition_failed_and_changes_nothing()
    {
        var displayName = _user.DisplayName;

        var result = await _sut.HandleAsync(
            new UpdateProfileCommand("Alicia", "en", "UTC") { IfMatch = IfMatch.Of([new EntityTag("stale")]) }, Ct);

        result.Error.ShouldBe(ConcurrencyErrors.PreconditionFailed);
        _user.DisplayName.ShouldBe(displayName);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_matching_if_match_updates()
    {
        var read = ProfileVersion.Of(_user.DisplayName, _user.Locale, _user.TimeZone);
        _getMe.HandleAsync(Arg.Any<GetMeQuery>(), Arg.Any<CancellationToken>())
            .Returns(new MeResponse(_user.Id, "alice@example.com", true, "Alicia", "en", "UTC", [], []));

        var result = await _sut.HandleAsync(new UpdateProfileCommand("Alicia", "en", "UTC") { IfMatch = IfMatch.Of([read]) }, Ct);

        result.IsSuccess.ShouldBeTrue();
        _user.DisplayName.ShouldBe("Alicia");
    }
}
