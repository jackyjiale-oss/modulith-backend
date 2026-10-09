using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Authentication.Logout;

/// <summary>Ends the caller's current session, the one named by the access token's <c>sid</c> claim.</summary>
internal sealed record LogoutCommand : ICommand;
