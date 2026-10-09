using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Passwords;
using TemplateName.Modules.Auth.Application.Me.Update;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Admin.Users.Create;

/// <summary>
/// Creates an account on an administrator's behalf. The account is registered like any other (<see cref="User.Register"/> raises
/// <c>UserRegisteredDomainEvent</c>, as for self-registration and the seeded administrator) but has no password: it is active, its
/// email is unconfirmed, and login treats it like an unknown address (Ruling R3) until the user sets a password through the reset link
/// issued here (<see cref="PasswordResetLinkIssuer"/>, the forgot-password flow and email); that reset also confirms the email.
/// <para>
/// The address is normalized as at registration, so <c>Alice@Example.com </c> and <c>alice@example.com</c> are one account; an address
/// in use answers <see cref="UserErrors.EmailTaken"/> (an administrator may learn that), also when a simultaneous create wins the race
/// on the unique index. Without role ids the account gets the <c>User</c> role. Role ids need <c>auth.user.assign_roles</c>, because
/// <c>Admin</c> may create users but not assign roles; each must exist and not be deleted, only a SuperAdmin may grant SuperAdmin, and
/// the actor must hold every permission the roles grant (<see cref="PermissionGrantRules"/>).
/// </para>
/// </summary>
internal sealed class CreateUserCommandHandler(
    IUserRepository users,
    IRoleRepository roles,
    IPermissionChecker permissionChecker,
    SuperAdminRules superAdminRules,
    PermissionGrantRules grantRules,
    PasswordResetLinkIssuer linkIssuer,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<CreateUserCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(CreateUserCommand command, CancellationToken cancellationToken)
    {
        if (await users.GetByNormalizedEmailAsync(User.NormalizeEmail(command.Email), cancellationToken) is not null)
        {
            return UserErrors.EmailTaken;
        }

        var roleIds = await ResolveRolesAsync(command, cancellationToken);
        if (roleIds.IsFailure)
        {
            return roleIds.Error;
        }

        var now = timeProvider.GetUtcNow();
        var locale = UpdateProfileCommandValidator.TryGetCanonicalLocale(command.Locale, out var canonical) ? canonical : command.Locale;
        var user = User.Register(command.Email, command.DisplayName.Trim(), locale, passwordHash: null, now).Value;
        foreach (var roleId in roleIds.Value)
        {
            user.AssignRole(roleId, command.ActorId, now);
        }

        users.Add(user);
        await linkIssuer.IssueAsync(user, VerificationTrigger.CreatedByAdmin, now, cancellationToken);
        auditWriter.Record(AdminAudit.Succeeded(AuthAuditEvents.AdminUserCreated, user.Id, command.ActorId, now));

        // One save: the user, its roles, the reset code, the audit entry and the outbox rows. A refusal by the email index means another
        // request created the address after the lookup above; any other unique violation is not "taken" and still throws.
        if (!await unitOfWork.SaveChangesUnlessDuplicateAsync(UniqueIndexNames.UserEmail, cancellationToken))
        {
            return UserErrors.EmailTaken;
        }

        return user.Id;
    }

    private async Task<Result<IReadOnlyList<Guid>>> ResolveRolesAsync(CreateUserCommand command, CancellationToken cancellationToken)
    {
        if (command.RoleIds is not { Count: > 0 })
        {
            var userRole = await roles.GetByNormalizedNameAsync(Role.NormalizeName(SystemRoles.User), cancellationToken)
                ?? throw new InvalidOperationException("The User system role does not exist; run the Auth module seeder (SeedAuthModuleAsync).");
            return Result.Success<IReadOnlyList<Guid>>([userRole.Id]);
        }

        if (!await permissionChecker.HasPermissionAsync(command.ActorId, AuthPermissions.UserAssignRoles, cancellationToken))
        {
            return UserErrors.RoleAssignmentNotAllowed;
        }

        var requested = command.RoleIds.Distinct().ToList();
        var found = (await roles.GetByIdsAsync(requested, cancellationToken)).ToDictionary(role => role.Id);
        if (requested.Where(id => !found.ContainsKey(id)).Select(id => (Guid?)id).FirstOrDefault() is { } unknownId)
        {
            return RoleErrors.NotFound(unknownId);
        }

        var scope = await superAdminRules.GetScopeAsync(command.ActorId, cancellationToken);
        var allowed = scope.EnsureCanGrant(requested);
        if (allowed.IsSuccess)
        {
            allowed = await grantRules.EnsureActorHoldsRolesAsync(command.ActorId, requested.Select(id => found[id]), cancellationToken);
        }

        return allowed.IsSuccess ? Result.Success<IReadOnlyList<Guid>>(requested) : Result.Failure<IReadOnlyList<Guid>>(allowed.Error);
    }
}
