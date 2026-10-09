using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Sessions.Revoke;

/// <summary>Ends one of the caller's own sessions, the current one included.</summary>
/// <param name="SessionId">The session to end.</param>
internal sealed record RevokeSessionCommand(Guid SessionId) : ICommand;
