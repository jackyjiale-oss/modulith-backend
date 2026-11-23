using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Application.Devices.List;

internal sealed class ListDevicesQueryHandler(IPushDeviceRepository devices, ICurrentUser currentUser)
    : IQueryHandler<ListDevicesQuery, DevicesResponse>
{
    public async Task<Result<DevicesResponse>> HandleAsync(ListDevicesQuery query, CancellationToken cancellationToken)
    {
        // A caller without a user id owns nothing.
        var active = await devices.ListActiveAsync(currentUser.UserId ?? Guid.Empty, cancellationToken);
        return new DevicesResponse([.. active.Select(DeviceResponse.From)]);
    }
}
