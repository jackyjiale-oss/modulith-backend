namespace TemplateName.Modules.Notifications.Domain.PushDevices;

/// <summary>Why a push device stopped receiving pushes. The values are persisted: never renumber or reuse them.</summary>
internal enum PushDeviceDeactivationReason : byte
{
    /// <summary>The user unregistered it (<c>DELETE /api/v1/notifications/devices/{id}</c>).</summary>
    RemovedByUser = 1,

    /// <summary>The sign-in session that registered it was logged out, revoked or expired.</summary>
    SessionEnded = 2,

    /// <summary>The push provider answered that the token is no longer valid (FCM <c>UNREGISTERED</c>, <c>SENDER_ID_MISMATCH</c>).</summary>
    RejectedByProvider = 3,

    /// <summary>The user registered more devices than <c>Notifications:Push:MaxDevicesPerUser</c>; the oldest made way.</summary>
    Replaced = 4,
}
