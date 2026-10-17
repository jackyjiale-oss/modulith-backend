using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Mfa.SetUpTotp;

/// <summary>
/// Starts (or restarts) the setup of an authenticator app for the signed-in user. <paramref name="Label"/> names it (<c>Authenticator
/// app</c> when absent); the method protects nothing until a code from the app confirms it.
/// </summary>
internal sealed record SetUpTotpCommand(string? Label) : ICommand<TotpSetupResponse>;
