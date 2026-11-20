using TemplateName.Application.Common.Concurrency;
using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Admin.Users.AssignRoles;

/// <summary>Replaces the roles of <paramref name="UserId"/> with <paramref name="RoleIds"/>, on behalf of <paramref name="ActorId"/>.</summary>
internal sealed record AssignRolesCommand(Guid ActorId, Guid UserId, IReadOnlyCollection<Guid> RoleIds) : ICommand<EntityTag>
{
    /// <summary>The <c>If-Match</c> precondition of the caller; none when the header is absent.</summary>
    public IfMatch IfMatch { get; init; } = IfMatch.None;
}
