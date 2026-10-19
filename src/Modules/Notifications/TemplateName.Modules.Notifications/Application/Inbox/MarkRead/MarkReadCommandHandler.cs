using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.InApp;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Application.Inbox.MarkRead;

/// <summary>
/// Marks one of the caller's notifications as read. A notification that does not exist and one of another user answer with the same
/// <see cref="InAppNotificationErrors.NotFound"/>, because the repository returns a row only to its owner, so ids cannot be probed. A
/// notification that is already read succeeds without changing anything and keeps its first read time: marking twice is not an error.
/// Two simultaneous calls are harmless (both end in "read"; the table has no concurrency token, so neither can fail).
/// </summary>
internal sealed class MarkReadCommandHandler(
    IInAppNotificationRepository notifications,
    ICurrentUser currentUser,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<MarkReadCommand>
{
    public async Task<Result> HandleAsync(MarkReadCommand command, CancellationToken cancellationToken)
    {
        // A caller without a user id owns nothing, so no row is returned for the empty id.
        var userId = currentUser.UserId ?? Guid.Empty;
        var notification = await notifications.GetForUserAsync(command.NotificationId, userId, cancellationToken);
        if (notification is null)
        {
            return Result.Failure(InAppNotificationErrors.NotFound(command.NotificationId));
        }

        if (notification.ReadAt is not null)
        {
            return Result.Success();
        }

        notification.MarkRead(timeProvider.GetUtcNow());

        // A mark is stored even when the client goes away.
        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        return Result.Success();
    }
}
