using NSubstitute;
using TemplateName.Modules.Auth.Application.Admin.Users.ForcePasswordReset;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth.Admin;

public sealed class ForcePasswordResetCommandHandlerTests : AdminHandlerTestBase
{
    private readonly ForcePasswordResetCommandHandler _sut;

    public ForcePasswordResetCommandHandlerTests()
    {
        _sut = new ForcePasswordResetCommandHandler(Users, Sessions, Rules, LinkIssuer, AuditWriter, UnitOfWork, Time);
    }

    [Fact]
    public async Task Issues_a_reset_code_replacing_pending_ones_revokes_every_session_and_audits()
    {
        var target = GivenUser(UserRole);
        var sessions = GivenActiveSessions(target, 2);
        var pending = VerificationCode.Issue(
            target.Id, VerificationPurpose.PasswordReset, target.NormalizedEmail, new byte[32], "old", TimeSpan.FromMinutes(30), null, Now.AddMinutes(-5));
        VerificationCodes.GetPendingAsync(target.Id, VerificationPurpose.PasswordReset, Now, Arg.Any<CancellationToken>()).Returns([pending]);

        var result = await _sut.HandleAsync(new ForcePasswordResetCommand(ActorId, target.Id), Ct);

        result.IsSuccess.ShouldBeTrue();
        pending.InvalidatedAt.ShouldBe(Now);
        var code = IssuedCodes.ShouldHaveSingleItem();
        code.Purpose.ShouldBe(VerificationPurpose.PasswordReset);
        code.ExpiresAt.ShouldBe(Now.AddMinutes(30));
        sessions.ShouldAllBe(session => session.RevokedReason == SessionRevokedReason.AdminRevoked);
        var audit = AuditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.AdminPasswordResetForced);
        audit.UserId.ShouldBe(target.Id);
        ActorOf(audit).ShouldBe(ActorId);
        audit.Details.ShouldNotBeNull().ShouldNotContain(TokenValue);
        await UnitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Suspended_account_is_refused_because_its_reset_would_be_refused()
    {
        var target = GivenUser(UserRole);
        target.Suspend(Now.AddDays(-1));

        var result = await _sut.HandleAsync(new ForcePasswordResetCommand(ActorId, target.Id), Ct);

        result.Error.Code.ShouldBe("auth.account_inactive");
        IssuedCodes.ShouldBeEmpty();
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Admin_cannot_force_a_reset_on_a_super_admin()
    {
        GivenActorIsSuperAdmin(false);
        var target = GivenUser(SuperAdminRole);

        var result = await _sut.HandleAsync(new ForcePasswordResetCommand(ActorId, target.Id), Ct);

        result.Error.Code.ShouldBe("auth.cannot_manage_super_admin");
        result.Error.Type.ShouldBe(ErrorType.Forbidden);
        IssuedCodes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unknown_user_returns_user_not_found()
    {
        var result = await _sut.HandleAsync(new ForcePasswordResetCommand(ActorId, Guid.NewGuid()), Ct);

        result.Error.Code.ShouldBe("auth.user_not_found");
    }
}
