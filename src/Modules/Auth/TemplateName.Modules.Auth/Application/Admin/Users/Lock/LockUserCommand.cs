using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Admin.Users.Lock;

/// <summary>Suspends <paramref name="UserId"/> on behalf of <paramref name="ActorId"/>, the signed-in administrator.</summary>
internal sealed record LockUserCommand(Guid ActorId, Guid UserId) : ICommand;
