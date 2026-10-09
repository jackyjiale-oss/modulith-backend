using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Registration.ResendConfirmation;

/// <summary>Sends a new confirmation link to an unconfirmed account; answers the same whether or not anything was sent.</summary>
internal sealed record ResendConfirmationCommand(string Email) : ICommand;
