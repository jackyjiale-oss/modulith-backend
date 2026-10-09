using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Admin.Roles.Create;

/// <summary>Creates a custom role without permissions on behalf of <paramref name="ActorId"/>. Answers the new role's id.</summary>
internal sealed record CreateRoleCommand(Guid ActorId, string Name, string Description) : ICommand<Guid>;
