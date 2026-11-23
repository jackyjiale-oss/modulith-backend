using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Notifications.Application.Devices.List;

internal sealed record ListDevicesQuery : IQuery<DevicesResponse>;

/// <summary>The caller's active devices, the latest registration first; not paged (at most <c>MaxDevicesPerUser</c>).</summary>
internal sealed record DevicesResponse(IReadOnlyList<DeviceResponse> Items);
