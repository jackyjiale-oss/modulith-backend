namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>
/// The module's counters (meter <c>TemplateName.Notifications</c>). Tags carry fixed values only (a type code from the catalog): never a
/// user id, an address or a link.
/// </summary>
internal interface INotificationsMetrics
{
    /// <summary>Counts one notification created by the scheduler (<c>notifications.created</c>, tag <c>type</c>).</summary>
    void RecordCreated(string typeCode);
}
