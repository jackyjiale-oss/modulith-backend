using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Authentication.LogoutAll;

/// <summary>Ends every active session of the caller, the current one included.</summary>
internal sealed record LogoutAllCommand : ICommand;
