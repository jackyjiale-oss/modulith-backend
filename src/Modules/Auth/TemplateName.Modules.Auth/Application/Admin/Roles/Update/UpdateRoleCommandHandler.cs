using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Admin.Roles.Update;

/// <summary>
/// Renames a role and replaces its description. A system role cannot be renamed (<see cref="RoleErrors.SystemRoleProtected"/>, decided
/// by <see cref="Role.UpdateDetails"/>); the new name must not belong to another role, ignoring case
/// (<see cref="RoleErrors.NameTaken"/>, also when a simultaneous rename wins the unique index). A rename changes no grant, so no user's
/// cached permissions are touched. A role that is unknown or deleted is <see cref="RoleErrors.NotFound"/>. Audited as
/// <c>auth.role_updated</c>, with the previous name, in the same save.
/// </summary>
internal sealed class UpdateRoleCommandHandler(
    IRoleRepository roles,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<UpdateRoleCommand>
{
    public async Task<Result> HandleAsync(UpdateRoleCommand command, CancellationToken cancellationToken)
    {
        var role = await roles.GetByIdAsync(command.RoleId, cancellationToken);
        if (role is null)
        {
            return Result.Failure(RoleErrors.NotFound(command.RoleId));
        }

        var previousName = role.Name;
        var name = command.Name.Trim();
        var updated = role.UpdateDetails(name, command.Description.Trim());
        if (updated.IsFailure)
        {
            return updated;
        }

        // The role is changed in memory only; nothing is saved when the name turns out to be taken.
        if (await roles.NameExistsAsync(Role.NormalizeName(name), role.Id, cancellationToken))
        {
            return Result.Failure(RoleErrors.NameTaken(name));
        }

        auditWriter.Record(RoleAudit.Updated(command.ActorId, role, previousName, timeProvider.GetUtcNow()));

        return await unitOfWork.SaveChangesUnlessDuplicateAsync(UniqueIndexNames.RoleName, cancellationToken)
            ? Result.Success()
            : Result.Failure(RoleErrors.NameTaken(name));
    }
}
