using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Passwords.Forgot;

/// <summary>Emails a password-reset link to the account with this address; answers the same whether or not anything was sent.</summary>
internal sealed record ForgotPasswordCommand(string Email) : ICommand;
