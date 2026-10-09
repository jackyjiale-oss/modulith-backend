using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Admin.Users.ForcePasswordReset;

/// <summary>Sends <paramref name="UserId"/> a reset link and ends their sessions, on behalf of <paramref name="ActorId"/>.</summary>
internal sealed record ForcePasswordResetCommand(Guid ActorId, Guid UserId) : ICommand;
