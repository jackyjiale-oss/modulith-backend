using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Admin.Roles.Delete;

/// <summary>
/// Deletes a custom role: <see cref="Role.MarkDeleted"/> refuses a system role (<see cref="RoleErrors.SystemRoleProtected"/>) and drops
/// the grants, then the role is removed, which the save interceptor turns into the soft delete (the row stays, hidden by the
/// <c>IsDeleted</c> filters; its name is free again). Users keep the assignment row, which no longer counts. After the save the cached
/// permissions of every user who held the role are removed, so they lose its permissions on their next request on this instance. A
/// role that is unknown or already deleted is <see cref="RoleErrors.NotFound"/>. Audited as <c>auth.role_deleted</c> in the same save.
/// </summary>
internal sealed class DeleteRoleCommandHandler(
    IRoleRepository roles,
    RolePermissionCacheInvalidator cacheInvalidator,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<DeleteRoleCommand>
{
    public async Task<Result> HandleAsync(DeleteRoleCommand command, CancellationToken cancellationToken)
    {
        var role = await roles.GetByIdAsync(command.RoleId, cancellationToken);
        if (role is null)
        {
            return Result.Failure(RoleErrors.NotFound(command.RoleId));
        }

        var deletable = role.MarkDeleted();
        if (deletable.IsFailure)
        {
            return deletable;
        }

        roles.Remove(role);
        auditWriter.Record(RoleAudit.Deleted(command.ActorId, role, timeProvider.GetUtcNow()));

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await cacheInvalidator.InvalidateAsync(role.Id);

        return Result.Success();
    }
}
