using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Admin.Users.ResetMfa;

/// <summary>Turns off the two-step verification of <paramref name="UserId"/> on behalf of <paramref name="ActorId"/>, the signed-in administrator.</summary>
internal sealed record ResetUserMfaCommand(Guid UserId, Guid ActorId) : ICommand;
