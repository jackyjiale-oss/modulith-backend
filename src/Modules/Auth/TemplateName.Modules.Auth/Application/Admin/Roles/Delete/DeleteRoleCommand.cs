using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Admin.Roles.Delete;

/// <summary>Soft-deletes the role <paramref name="RoleId"/> on behalf of <paramref name="ActorId"/>.</summary>
internal sealed record DeleteRoleCommand(Guid ActorId, Guid RoleId) : ICommand;
