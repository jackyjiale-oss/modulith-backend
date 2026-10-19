using NSubstitute;
using TemplateName.Modules.Auth.Application.Admin.Users.ResetMfa;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth.Admin;

public sealed class ResetUserMfaCommandHandlerTests : AdminHandlerTestBase
{
    private readonly IMfaMethodRepository _methods = Substitute.For<IMfaMethodRepository>();
    private readonly IRecoveryCodeRepository _recoveryCodes = Substitute.For<IRecoveryCodeRepository>();
    private readonly List<UserMfaMethod> _removedMethods = [];
    private readonly List<RecoveryCode> _removedCodes = [];
    private readonly ResetUserMfaCommandHandler _sut;

    public ResetUserMfaCommandHandlerTests()
    {
        _methods.Remove(Arg.Do<UserMfaMethod>(_removedMethods.Add));
        _recoveryCodes.Remove(Arg.Do<RecoveryCode>(_removedCodes.Add));
        _recoveryCodes.ListByUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);

        _sut = new ResetUserMfaCommandHandler(
            Users,
            _methods,
            _recoveryCodes,
            Rules,
            new MfaChangeEffects(Sessions),
            AuditWriter,
            UnitOfWork,
            Time);
    }

    [Fact]
    public async Task Cannot_reset_own_mfa()
    {
        var self = GivenUser(UserRole);
        Users.GetByIdAsync(ActorId, Arg.Any<CancellationToken>()).Returns(self);

        var result = await _sut.HandleAsync(new ResetUserMfaCommand(ActorId, ActorId), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("auth.cannot_reset_own_mfa");
        result.Error.Type.ShouldBe(ErrorType.Validation);
        await Users.DidNotReceiveWithAnyArgs().GetByIdAsync(default, Ct);
        _removedMethods.ShouldBeEmpty();
        AuditEntries.ShouldBeEmpty();
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Unknown_user_returns_user_not_found()
    {
        var result = await _sut.HandleAsync(new ResetUserMfaCommand(Guid.NewGuid(), ActorId), Ct);

        result.Error.Code.ShouldBe("auth.user_not_found");
        result.Error.Type.ShouldBe(ErrorType.NotFound);
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Admin_cannot_reset_a_super_admin()
    {
        GivenActorIsSuperAdmin(false);
        var target = GivenUser(SuperAdminRole);
        target.ClearDomainEvents();
        GivenConfirmedMethod(target);

        var result = await _sut.HandleAsync(new ResetUserMfaCommand(target.Id, ActorId), Ct);

        result.Error.Code.ShouldBe("auth.cannot_manage_super_admin");
        result.Error.Type.ShouldBe(ErrorType.Forbidden);
        _removedMethods.ShouldBeEmpty();
        target.DomainEvents.ShouldBeEmpty();
        AuditEntries.ShouldBeEmpty();
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Super_admin_can_reset_another_super_admin()
    {
        GivenActorIsSuperAdmin();
        var target = GivenUser(SuperAdminRole);
        GivenConfirmedMethod(target);

        var result = await _sut.HandleAsync(new ResetUserMfaCommand(target.Id, ActorId), Ct);

        result.IsSuccess.ShouldBeTrue();
        _removedMethods.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task User_without_mfa_is_a_no_op()
    {
        var target = GivenUser(UserRole);
        var stamp = target.SecurityStamp;
        target.ClearDomainEvents();
        var sessions = GivenActiveSessions(target, 2);

        var result = await _sut.HandleAsync(new ResetUserMfaCommand(target.Id, ActorId), Ct);

        result.IsSuccess.ShouldBeTrue();
        target.SecurityStamp.ShouldBe(stamp);
        target.DomainEvents.ShouldBeEmpty();
        sessions.ShouldAllBe(session => session.RevokedAt == null);
        _removedMethods.ShouldBeEmpty();
        _removedCodes.ShouldBeEmpty();
        AuditEntries.ShouldBeEmpty();
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Reset_removes_methods_and_codes_rotates_the_stamp_and_revokes_every_session()
    {
        var target = GivenUser(UserRole);
        var stamp = target.SecurityStamp;
        target.ClearDomainEvents();
        var method = GivenConfirmedMethod(target);
        var codes = new[]
        {
            RecoveryCode.Create(target.Id, RecoveryCodeFormat.Hash(RecoveryCodeFormat.Generate()), Now.AddDays(-9)),
            RecoveryCode.Create(target.Id, RecoveryCodeFormat.Hash(RecoveryCodeFormat.Generate()), Now.AddDays(-9)),
        };
        _recoveryCodes.ListByUserAsync(target.Id, Arg.Any<CancellationToken>()).Returns(codes);
        var sessions = GivenActiveSessions(target, 3);

        var result = await _sut.HandleAsync(new ResetUserMfaCommand(target.Id, ActorId), Ct);

        result.IsSuccess.ShouldBeTrue();
        _removedMethods.ShouldHaveSingleItem().ShouldBeSameAs(method);
        _removedCodes.ShouldBe(codes);
        target.SecurityStamp.ShouldNotBe(stamp);
        target.DomainEvents.ShouldHaveSingleItem().ShouldBe(new MfaChangedDomainEvent(target.Id, MfaChange.ResetByAdmin));
        sessions.ShouldAllBe(session => session.RevokedReason == SessionRevokedReason.AdminRevoked && session.RevokedAt == Now);
        var audit = AuditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.AdminMfaReset);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(target.Id);
        ActorOf(audit).ShouldBe(ActorId);

        // One save, with no cancellation token: a reset is stored even when the client goes away.
        await UnitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Pending_method_alone_is_removed_without_rotating_the_stamp()
    {
        var target = GivenUser(UserRole);
        var stamp = target.SecurityStamp;
        target.ClearDomainEvents();
        var pending = UserMfaMethod.StartTotp(target.Id, "Phone", "protected-secret", Now.AddMinutes(-1));
        _methods.GetTotpAsync(target.Id, Arg.Any<CancellationToken>()).Returns(pending);
        var sessions = GivenActiveSessions(target, 2);

        var result = await _sut.HandleAsync(new ResetUserMfaCommand(target.Id, ActorId), Ct);

        result.IsSuccess.ShouldBeTrue();
        _removedMethods.ShouldHaveSingleItem().ShouldBeSameAs(pending);
        target.SecurityStamp.ShouldBe(stamp);
        target.DomainEvents.ShouldBeEmpty();
        sessions.ShouldAllBe(session => session.RevokedAt == null);
        AuditEntries.ShouldHaveSingleItem().EventType.ShouldBe(AuthAuditEvents.AdminMfaReset);
        await UnitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
    }

    private UserMfaMethod GivenConfirmedMethod(User owner)
    {
        var method = UserMfaMethod.StartTotp(owner.Id, "Phone", "protected-secret", Now.AddMinutes(-30));
        method.Confirm(Totp.TimeStepAt(Now) - 5, Now.AddMinutes(-29)).IsSuccess.ShouldBeTrue();
        _methods.GetTotpAsync(owner.Id, Arg.Any<CancellationToken>()).Returns(method);
        return method;
    }
}
