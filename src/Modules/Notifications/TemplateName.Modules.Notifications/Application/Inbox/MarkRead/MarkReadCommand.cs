using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Notifications.Application.Inbox.MarkRead;

/// <summary>Marks one of the caller's notifications as read.</summary>
/// <param name="NotificationId">The notification to mark.</param>
internal sealed record MarkReadCommand(Guid NotificationId) : ICommand;
