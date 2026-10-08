using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Admin.Users.RevokeSessions;

/// <summary>Ends every session of <paramref name="UserId"/> on behalf of <paramref name="ActorId"/>, the signed-in administrator.</summary>
internal sealed record RevokeUserSessionsCommand(Guid ActorId, Guid UserId) : ICommand;
