using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Notifications.Application.Admin.Deliveries.Retry;

/// <summary>Puts a dead-lettered delivery back in the queue, due now, with a fresh attempt budget.</summary>
/// <param name="DeliveryId">The delivery to retry.</param>
internal sealed record RetryDeliveryCommand(Guid DeliveryId) : ICommand;
