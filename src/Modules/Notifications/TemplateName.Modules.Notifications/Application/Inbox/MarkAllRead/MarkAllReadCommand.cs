using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Notifications.Application.Inbox.MarkAllRead;

/// <summary>Marks every unread notification of the caller that exists at the server's current time as read.</summary>
internal sealed record MarkAllReadCommand : ICommand;
