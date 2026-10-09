using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Admin.Roles.Get;

/// <summary>
/// Reads one role through the repositories, whose soft-delete filter hides a deleted role: an unknown and a deleted id both answer
/// <see cref="RoleErrors.NotFound"/>.
/// </summary>
internal sealed class GetRoleQueryHandler(IRoleRepository roles, IPermissionRepository permissions) : IQueryHandler<GetRoleQuery, RoleResponse>
{
    public async Task<Result<RoleResponse>> HandleAsync(GetRoleQuery query, CancellationToken cancellationToken)
    {
        var role = await roles.GetByIdAsync(query.RoleId, cancellationToken);
        if (role is null)
        {
            return RoleErrors.NotFound(query.RoleId);
        }

        var granted = await permissions.GetByIdsAsync([.. role.Permissions.Select(grant => grant.PermissionId)], cancellationToken);

        return new RoleResponse(
            role.Id,
            role.Name,
            role.Description,
            role.IsSystem,
            role.CreatedAt,
            [.. granted.OrderBy(permission => permission.Code, StringComparer.Ordinal).Select(permission => new RolePermissionResponse(permission.Id, permission.Code, permission.IsDeprecated))]);
    }
}
