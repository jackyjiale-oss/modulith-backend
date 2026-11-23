using TemplateName.Modules.Auth.Contracts.Users;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Domain.PushDevices;

namespace TemplateName.Modules.Notifications.Infrastructure.Channels.Push;

/// <summary>
/// Delivers the <see cref="NotificationChannel.Push"/> deliveries, one device each. The device is loaded in the worker's scope, so a
/// deactivation is saved in the same save as the delivery's outcome. In order: a device that no longer exists is
/// <c>device_not_found</c>, an inactive one <c>device_inactive</c>, one that another user registered since (a phone that changed hands)
/// <c>device_reassigned</c> (so a queued alert never reaches the new owner), and one whose session ended is deactivated and
/// <c>session_ended</c>; all permanent. Otherwise <see cref="IPushTransport"/> sends it; a provider rejection that names the token
/// invalid deactivates the device.
/// </summary>
internal sealed class PushChannel(
    IPushDeviceRepository devices,
    IUserSessionDirectory sessions,
    IPushTransport transport,
    TimeProvider timeProvider) : INotificationChannel
{
    internal const int MaxTitleLength = 200;

    internal const int MaxBodyLength = 1000;

    public NotificationChannel Channel => NotificationChannel.Push;

    public async Task<ChannelSendResult> SendAsync(DeliveryWork work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        var device = PushDestination.IsValid(work.Destination)
            ? await devices.GetAsync(PushDestination.DeviceIdOf(work.Destination), cancellationToken)
            : null;
        if (device is null)
        {
            return ChannelSendResult.Permanent("device_not_found");
        }

        if (!device.IsActive)
        {
            return ChannelSendResult.Permanent("device_inactive");
        }

        if (device.UserId != work.RecipientUserId)
        {
            return ChannelSendResult.Permanent("device_reassigned");
        }

        var active = await sessions.GetActiveSessionIdsAsync(device.UserId, [device.SessionId], cancellationToken);
        if (!active.Contains(device.SessionId))
        {
            device.Deactivate(PushDeviceDeactivationReason.SessionEnded, timeProvider.GetUtcNow());
            return ChannelSendResult.Permanent("session_ended");
        }

        var message = new PushMessage(
            work.DeliveryId,
            device.Id,
            device.Token,
            BoundedText.Cut(work.Message.Subject, MaxTitleLength),
            BoundedText.Cut(work.Message.TextBody, MaxBodyLength),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["notificationId"] = work.NotificationId.ToString("D"),
                ["typeCode"] = work.TypeCode,
                ["category"] = work.Category.ToString(),
            },
            Urgent: work.Priority is NotificationPriority.Critical or NotificationPriority.High);
        try
        {
            await transport.SendAsync(message, cancellationToken);
            return ChannelSendResult.Sent;
        }
        catch (PushSendException failure)
        {
            if (failure.DeactivateDevice)
            {
                device.Deactivate(PushDeviceDeactivationReason.RejectedByProvider, timeProvider.GetUtcNow());
            }

            return failure.IsPermanent ? ChannelSendResult.Permanent(failure.Reason) : ChannelSendResult.Transient(failure.Reason);
        }
    }
}
