using TemplateName.Application.Common.Concurrency;
using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Admin.Roles.SetPermissions;

/// <summary>Replaces the permissions of the role <paramref name="RoleId"/> with <paramref name="PermissionIds"/>, on behalf of <paramref name="ActorId"/>.</summary>
internal sealed record SetRolePermissionsCommand(Guid ActorId, Guid RoleId, IReadOnlyCollection<Guid> PermissionIds) : ICommand<EntityTag>
{
    /// <summary>The caller's <c>If-Match</c> precondition; none when the header is absent.</summary>
    public IfMatch IfMatch { get; init; } = IfMatch.None;
}
