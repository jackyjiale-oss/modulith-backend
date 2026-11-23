using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.PushDevices;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Application.Devices.Unregister;

/// <summary>
/// Deactivates one of the caller's devices with <see cref="PushDeviceDeactivationReason.RemovedByUser"/>; the row stays until the
/// cleanup job. An unknown id and another user's device both answer <c>notifications.device_not_found</c>; a device already inactive
/// answers success and keeps its first reason.
/// </summary>
internal sealed class UnregisterDeviceCommandHandler(
    IPushDeviceRepository devices,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider) : ICommandHandler<UnregisterDeviceCommand>
{
    public async Task<Result> HandleAsync(UnregisterDeviceCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var device = await devices.GetAsync(command.Id, cancellationToken);
        if (device is null || currentUser.UserId is not { } userId || device.UserId != userId)
        {
            return Result.Failure(PushDeviceErrors.NotFound(command.Id));
        }

        if (!device.IsActive)
        {
            return Result.Success();
        }

        device.Deactivate(PushDeviceDeactivationReason.RemovedByUser, timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
