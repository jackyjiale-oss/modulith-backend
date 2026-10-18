using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;

namespace TemplateName.Modules.Auth.Application.TrustedDevices.List;

/// <summary>A page of the caller's usable trusted devices.</summary>
internal sealed record ListTrustedDevicesQuery(CursorPageRequest Page) : IQuery<CursorPage<TrustedDeviceResponse>>;
