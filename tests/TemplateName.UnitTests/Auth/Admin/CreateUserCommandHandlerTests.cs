using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Admin.Users.Create;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.Modules.Auth.Domain.Verification.Events;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth.Admin;

public sealed class CreateUserCommandHandlerTests : AdminHandlerTestBase
{
    private const string Email = "  New.Person@Example.com ";

    private readonly List<User> _added = [];
    private readonly CreateUserCommandHandler _sut;

    public CreateUserCommandHandlerTests()
    {
        Users.Add(Arg.Do<User>(_added.Add));
        UnitOfWork.SaveChangesUnlessDuplicateAsync(UniqueIndexNames.UserEmail, Arg.Any<CancellationToken>()).Returns(true);
        _sut = new CreateUserCommandHandler(Users, Roles, PermissionChecker, Rules, GrantRules, LinkIssuer, AuditWriter, UnitOfWork, Time);
    }

    [Fact]
    public async Task Create_with_existing_email_returns_email_taken()
    {
        var existing = GivenUser(UserRole);
        Users.GetByNormalizedEmailAsync(User.NormalizeEmail(Email), Arg.Any<CancellationToken>()).Returns(existing);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("auth.email_taken");
        result.Error.Type.ShouldBe(ErrorType.Conflict);
        _added.ShouldBeEmpty();
        IssuedCodes.ShouldBeEmpty();
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesUnlessDuplicateAsync(default!, Ct);
    }

    [Fact]
    public async Task Email_taken_by_a_simultaneous_create_is_reported_as_email_taken()
    {
        // Both requests passed the lookup; the unique index on NormalizedEmail refused the second insert.
        UnitOfWork.SaveChangesUnlessDuplicateAsync(UniqueIndexNames.UserEmail, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.Code.ShouldBe("auth.email_taken");
    }

    [Fact]
    public async Task Creates_an_active_unconfirmed_user_without_a_password_with_the_user_role_a_reset_code_and_an_audit_row()
    {
        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        var user = _added.ShouldHaveSingleItem();
        result.Value.ShouldBe(user.Id);
        user.Email.ShouldBe("New.Person@Example.com");
        user.NormalizedEmail.ShouldBe("NEW.PERSON@EXAMPLE.COM");
        user.PasswordHash.ShouldBeNull();
        user.PasswordHistory.ShouldBeEmpty();
        user.EmailConfirmed.ShouldBeFalse();
        user.Status.ShouldBe(UserStatus.Active);
        user.DisplayName.ShouldBe("New Person");
        user.Locale.ShouldBe("ms");
        user.DomainEvents.OfType<UserRegisteredDomainEvent>().ShouldHaveSingleItem();
        var assignment = user.Roles.ShouldHaveSingleItem();
        assignment.RoleId.ShouldBe(UserRole.Id);
        assignment.AssignedBy.ShouldBe(ActorId);

        // The set-password link is a normal reset code with the reset lifetime; only its hash is stored, the token travels encrypted.
        var code = IssuedCodes.ShouldHaveSingleItem();
        code.UserId.ShouldBe(user.Id);
        code.Purpose.ShouldBe(VerificationPurpose.PasswordReset);
        code.Target.ShouldBe(user.NormalizedEmail);
        code.TokenHash.ShouldBe(TokenHash);
        code.ExpiresAt.ShouldBe(Now.AddMinutes(30));
        code.DomainEvents.OfType<VerificationCodeIssuedDomainEvent>().ShouldHaveSingleItem().ProtectedToken.ShouldBe("protected:" + TokenValue);

        var audit = AuditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.AdminUserCreated);
        audit.UserId.ShouldBe(user.Id);
        ActorOf(audit).ShouldBe(ActorId);
        audit.Details.ShouldNotBeNull().ShouldNotContain(TokenValue);
        await UnitOfWork.Received(1).SaveChangesUnlessDuplicateAsync(UniqueIndexNames.UserEmail, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Role_ids_are_assigned_instead_of_the_default_when_the_actor_may_assign_roles()
    {
        var support = GivenRole("Support");
        GivenActorMayAssignRoles();

        var result = await _sut.HandleAsync(Command([support.Id, support.Id]), Ct);

        result.IsSuccess.ShouldBeTrue();
        _added.ShouldHaveSingleItem().Roles.Select(role => role.RoleId).ShouldBe([support.Id]);
    }

    [Fact]
    public async Task Role_ids_with_a_permission_the_actor_lacks_return_permission_grant_not_allowed()
    {
        // assign_roles alone must not let its holder create accounts that hold more than they do.
        var manage = GivenPermission("auth.role.manage");
        var view = GivenPermission("auth.user.view");
        var powerful = GivenRole("Powerful", manage, view);
        GivenActorMayAssignRoles();
        ActorHolds.Add("auth.user.view");

        var result = await _sut.HandleAsync(Command([powerful.Id]), Ct);

        result.Error.Code.ShouldBe("auth.permission_grant_not_allowed");
        result.Error.Type.ShouldBe(ErrorType.Forbidden);
        _added.ShouldBeEmpty();
        IssuedCodes.ShouldBeEmpty();
        AuditEntries.ShouldBeEmpty();
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesUnlessDuplicateAsync(default!, Ct);
    }

    [Fact]
    public async Task Role_ids_whose_permissions_the_actor_holds_are_assigned()
    {
        var view = GivenPermission("auth.user.view");
        var support = GivenRole("Support", view);
        GivenActorMayAssignRoles();
        ActorHolds.Add("auth.user.view");

        var result = await _sut.HandleAsync(Command([support.Id]), Ct);

        result.IsSuccess.ShouldBeTrue();
        _added.ShouldHaveSingleItem().Roles.ShouldHaveSingleItem().RoleId.ShouldBe(support.Id);
    }

    [Fact]
    public async Task Role_ids_require_the_assign_roles_permission()
    {
        // Admin may create users but not assign roles; otherwise it could create accounts holding roles it cannot grant.
        var support = GivenRole("Support");

        var result = await _sut.HandleAsync(Command([support.Id]), Ct);

        result.Error.Code.ShouldBe("auth.role_assignment_not_allowed");
        result.Error.Type.ShouldBe(ErrorType.Forbidden);
        _added.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unknown_role_returns_role_not_found()
    {
        GivenActorMayAssignRoles();
        var unknown = Guid.NewGuid();

        var result = await _sut.HandleAsync(Command([unknown]), Ct);

        result.Error.Code.ShouldBe("auth.role_not_found");
        _added.ShouldBeEmpty();
    }

    [Fact]
    public async Task Non_super_admin_cannot_create_a_super_admin()
    {
        GivenActorMayAssignRoles();
        GivenActorIsSuperAdmin(false);

        var result = await _sut.HandleAsync(Command([SuperAdminRole.Id]), Ct);

        result.Error.Code.ShouldBe("auth.cannot_manage_super_admin");
        _added.ShouldBeEmpty();
    }

    [Fact]
    public async Task Super_admin_can_create_a_super_admin()
    {
        GivenActorMayAssignRoles();
        GivenActorIsSuperAdmin();

        var result = await _sut.HandleAsync(Command([SuperAdminRole.Id]), Ct);

        result.IsSuccess.ShouldBeTrue();
        _added.ShouldHaveSingleItem().Roles.ShouldHaveSingleItem().RoleId.ShouldBe(SuperAdminRole.Id);
    }

    private CreateUserCommand Command(IReadOnlyCollection<Guid>? roleIds = null)
        => new(ActorId, Email, " New Person ", "ms", roleIds);

    private void GivenActorMayAssignRoles() => ActorHolds.Add(AuthPermissions.UserAssignRoles);
}
