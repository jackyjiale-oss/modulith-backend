using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.TrustedDevices.Revoke;

/// <summary>Forgets one of the caller's trusted devices: its token no longer skips the challenge.</summary>
/// <param name="TrustedDeviceId">The device to forget.</param>
internal sealed record RevokeTrustedDeviceCommand(Guid TrustedDeviceId) : ICommand;
