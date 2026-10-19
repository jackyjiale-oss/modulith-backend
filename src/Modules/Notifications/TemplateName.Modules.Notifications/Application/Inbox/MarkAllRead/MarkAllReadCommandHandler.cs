using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Application.Inbox.MarkAllRead;

/// <summary>
/// Marks the caller's unread notifications created at or before the server's <c>now</c> as read, in one statement
/// (<see cref="IInAppNotificationRepository.MarkAllReadAsync"/>); one that arrives later stays unread, so a notification the user has not
/// seen yet is not silently marked. Succeeds when there is nothing to mark. A caller without a user id marks nothing.
/// </summary>
internal sealed class MarkAllReadCommandHandler(
    IInAppNotificationRepository notifications,
    ICurrentUser currentUser,
    TimeProvider timeProvider) : ICommandHandler<MarkAllReadCommand>
{
    public async Task<Result> HandleAsync(MarkAllReadCommand command, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? Guid.Empty;

        // One UPDATE that is stored even when the client goes away, like a single mark.
        await notifications.MarkAllReadAsync(userId, timeProvider.GetUtcNow(), CancellationToken.None);

        return Result.Success();
    }
}
