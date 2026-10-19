using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Domain.Deliveries;

/// <summary>The delivery errors. Each code has a message in <c>Resources/NotificationsErrorMessages.resx</c> and its translations.</summary>
internal static class DeliveryErrors
{
    /// <summary>Only a dead-lettered delivery can be retried; a pending, sent or expired one cannot.</summary>
    public static readonly Error NotRetryable = Error.Conflict(
        "notifications.delivery_not_retryable",
        "Only a dead-lettered delivery can be retried.");

    /// <summary>The notification expired, so sending it now would deliver something stale (for example a dead reset link).</summary>
    public static readonly Error Expired = Error.Conflict(
        "notifications.delivery_expired",
        "The delivery has expired and cannot be retried.");

    public static Error NotFound(Guid id) =>
        Error.NotFound("notifications.delivery_not_found", $"Delivery '{id}' was not found.")
        with
        {
            Parameters = new Dictionary<string, object?> { ["id"] = id },
        };
}
