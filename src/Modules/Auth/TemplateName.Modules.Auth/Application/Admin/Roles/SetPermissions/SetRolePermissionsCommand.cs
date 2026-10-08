using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Admin.Roles.SetPermissions;

/// <summary>Replaces the permissions of the role <paramref name="RoleId"/> with <paramref name="PermissionIds"/>, on behalf of <paramref name="ActorId"/>.</summary>
internal sealed record SetRolePermissionsCommand(Guid ActorId, Guid RoleId, IReadOnlyCollection<Guid> PermissionIds) : ICommand;
