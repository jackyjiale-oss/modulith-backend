using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Sessions.Events;

/// <summary>
/// A user signed in from a device that none of their earlier sessions came from. <c>IpAddress</c> is the address the server observed;
/// the event carries no client-supplied text such as the device name.
/// </summary>
internal sealed record NewDeviceSignedInDomainEvent(Guid UserId, Guid SessionId, string? IpAddress) : IDomainEvent;
