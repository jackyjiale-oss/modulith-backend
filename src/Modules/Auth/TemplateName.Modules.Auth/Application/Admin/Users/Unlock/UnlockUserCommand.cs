using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Admin.Users.Unlock;

/// <summary>Reinstates <paramref name="UserId"/> on behalf of <paramref name="ActorId"/>, the signed-in administrator.</summary>
internal sealed record UnlockUserCommand(Guid ActorId, Guid UserId) : ICommand;
