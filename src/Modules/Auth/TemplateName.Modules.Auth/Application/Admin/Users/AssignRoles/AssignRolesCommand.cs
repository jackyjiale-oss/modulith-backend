using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Admin.Users.AssignRoles;

/// <summary>Replaces the roles of <paramref name="UserId"/> with <paramref name="RoleIds"/>, on behalf of <paramref name="ActorId"/>.</summary>
internal sealed record AssignRolesCommand(Guid ActorId, Guid UserId, IReadOnlyCollection<Guid> RoleIds) : ICommand;
