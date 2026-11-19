using TemplateName.Application.Common.Concurrency;
using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Admin.Roles.Update;

/// <summary>Renames the role <paramref name="RoleId"/> and replaces its description, on behalf of <paramref name="ActorId"/>.</summary>
internal sealed record UpdateRoleCommand(Guid ActorId, Guid RoleId, string Name, string Description) : ICommand<EntityTag>
{
    /// <summary>The caller's <c>If-Match</c> precondition; none when the header is absent.</summary>
    public IfMatch IfMatch { get; init; } = IfMatch.None;
}
