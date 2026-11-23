using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Preferences;
using TemplateName.Modules.Notifications.Domain.PushDevices;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Application.Devices.Register;

/// <summary>
/// Registers the caller's device, bound to the access token's session (<c>sid</c>), in one save:
/// <list type="number">
/// <item>The device with the same token hash, whoever owns it, moves to the caller and the current session and is active again (a phone
/// signed in again, or one that changed hands); otherwise a new device is added.</item>
/// <item>Beyond <see cref="PushOptions.MaxDevicesPerUser"/> active devices, the oldest registrations are deactivated with
/// <see cref="PushDeviceDeactivationReason.Replaced"/>.</item>
/// <item>Two registrations of one token at once collide on the unique hash: <see cref="ConflictRetry"/> stages the registration again
/// from fresh reads, and a conflict that survives its attempts reaches the host as 409.</item>
/// </list>
/// </summary>
internal sealed class RegisterDeviceCommandHandler(
    IPushDeviceRepository devices,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    IOptions<PushOptions> options) : ICommandHandler<RegisterDeviceCommand, DeviceResponse>
{
    public async Task<Result<DeviceResponse>> HandleAsync(RegisterDeviceCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (currentUser.UserId is not { } userId || currentUser.SessionId is not { } sessionId)
        {
            return Result.Failure<DeviceResponse>(UnauthenticatedCaller.Error);
        }

        var tokenHash = PushDevice.HashOf(command.Token);
        PushDevice? registered = null;
        await ConflictRetry.SaveAsync(
            unitOfWork,
            async token =>
            {
                var now = timeProvider.GetUtcNow();
                var device = await devices.FindByTokenHashAsync(tokenHash, token);
                if (device is null)
                {
                    device = PushDevice.Register(userId, sessionId, command.Platform, command.Token, command.DeviceName, command.AppVersion, now);
                    devices.Add(device);
                }
                else
                {
                    device.Reregister(userId, sessionId, command.Platform, command.DeviceName, command.AppVersion, now);
                }

                var others = (await devices.ListActiveAsync(userId, token)).Where(other => other.Id != device.Id).ToList();
                foreach (var oldest in others
                    .OrderBy(other => other.LastRegisteredAt)
                    .Take(Math.Max(0, others.Count + 1 - options.Value.MaxDevicesPerUser)))
                {
                    oldest.Deactivate(PushDeviceDeactivationReason.Replaced, now);
                }

                registered = device;
            },
            cancellationToken);

        return DeviceResponse.From(registered!);
    }
}
