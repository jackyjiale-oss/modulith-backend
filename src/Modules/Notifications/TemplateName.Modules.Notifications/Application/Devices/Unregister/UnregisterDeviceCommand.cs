using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Notifications.Application.Devices.Unregister;

internal sealed record UnregisterDeviceCommand(Guid Id) : ICommand;
