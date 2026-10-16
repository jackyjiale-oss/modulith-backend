using Microsoft.Extensions.Logging;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Preferences;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Application.Admin.Deliveries.Retry;

/// <summary>
/// Puts a dead-lettered delivery back in the queue (<see cref="Delivery.Retry"/>: pending, due now, no attempts counted, no lease, no
/// error). The delivery worker then sends it like any other due delivery, so the retry itself sends nothing. A delivery that does not
/// exist answers <see cref="DeliveryErrors.NotFound"/>; one that is not dead-lettered (pending, sent or expired) answers
/// <see cref="DeliveryErrors.NotRetryable"/>, and a dead-lettered one whose notification has expired answers
/// <see cref="DeliveryErrors.Expired"/>, because its message would be stale.
/// </summary>
/// <remarks>
/// Two retries of one delivery at once (a double click, two administrators) put it back once: the delivery's status is a concurrency
/// token, so the second save finds the row no longer dead-lettered, and <see cref="ConflictRetry"/> reads it again and answers
/// <see cref="DeliveryErrors.NotRetryable"/> (409). A retry that races the worker cannot restore a delivery the worker has since
/// sent, for the same reason. The notification module keeps no audit log: a retry shows in the delivery row (a new attempt budget, then
/// its outcome) and in one log line naming the delivery and the administrator, never an address.
/// </remarks>
internal sealed partial class RetryDeliveryCommandHandler(
    INotificationRepository notifications,
    ICurrentUser currentUser,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<RetryDeliveryCommandHandler> logger) : ICommandHandler<RetryDeliveryCommand>
{
    public async Task<Result> HandleAsync(RetryDeliveryCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var outcome = Result.Success();

        // Each attempt reads the delivery again, so after a lost race the answer reflects what the winner did. A refusal changes
        // nothing, so the save that follows it has nothing to write.
        await ConflictRetry.SaveAsync(
            unitOfWork,
            async token =>
            {
                var delivery = await notifications.GetDeliveryAsync(command.DeliveryId, token);
                outcome = delivery is null ? Result.Failure(DeliveryErrors.NotFound(command.DeliveryId)) : delivery.Retry(now);
            },
            cancellationToken);

        if (outcome.IsSuccess)
        {
            LogDeliveryRetried(logger, command.DeliveryId, currentUser.UserId);
        }

        return outcome;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Delivery {DeliveryId} was put back in the queue by administrator {AdministratorId}")]
    private static partial void LogDeliveryRetried(ILogger logger, Guid deliveryId, Guid? administratorId);
}
