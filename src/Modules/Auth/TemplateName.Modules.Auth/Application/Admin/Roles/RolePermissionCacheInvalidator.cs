using TemplateName.Modules.Auth.Application.Abstractions;

namespace TemplateName.Modules.Auth.Application.Admin.Roles;

/// <summary>
/// Removes the cached permission set of every user who holds a role, so a change to the role's grants (or its deletion) applies to
/// their next request on this instance (ADR 0016; other instances within 30 seconds). Call it after the save: before it, a request
/// could reload the old set and cache it again. It never takes a cancellation token, so a client that goes away after the save cannot
/// leave stale entries. It also reaches users who are suspended or deleted, which is harmless: their set is empty anyway.
/// </summary>
internal sealed class RolePermissionCacheInvalidator(IRoleRepository roles, IPermissionCache permissionCache)
{
    public async Task InvalidateAsync(Guid roleId)
    {
        var userIds = await roles.GetUserIdsInRoleAsync(roleId, CancellationToken.None);
        await permissionCache.InvalidateUsersAsync(userIds, CancellationToken.None);
    }
}
