namespace TemplateName.Modules.Notifications.Application;

/// <summary>
/// The permission codes of the Notifications module, <c>module.resource.action</c> in snake case. The administration routes require
/// them by constant.
/// </summary>
internal static class NotificationsPermissions
{
    /// <summary>List deliveries (<c>GET admin/notifications/deliveries</c>).</summary>
    public const string DeliveryView = "notifications.delivery.view";

    /// <summary>Put a dead-lettered delivery back in the queue (<c>POST admin/notifications/deliveries/{id}/retry</c>).</summary>
    public const string DeliveryRetry = "notifications.delivery.retry";
}
