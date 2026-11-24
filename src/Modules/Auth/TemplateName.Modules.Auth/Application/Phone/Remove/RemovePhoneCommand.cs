using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Phone.Remove;

/// <summary>Removes the caller's verified number and ends any code waiting for a new one.</summary>
internal sealed record RemovePhoneCommand : ICommand;
