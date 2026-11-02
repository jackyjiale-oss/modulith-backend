using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.ExternalLogins.Unlink;

/// <summary>Removes the signed-in user's external login <paramref name="Id"/>.</summary>
internal sealed record UnlinkExternalLoginCommand(Guid Id) : ICommand;
