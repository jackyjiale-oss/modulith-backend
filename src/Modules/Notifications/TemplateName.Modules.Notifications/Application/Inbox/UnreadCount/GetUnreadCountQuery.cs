using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Notifications.Application.Inbox.UnreadCount;

/// <summary>The number of unread notifications in the caller's inbox, counted up to <see cref="GetUnreadCountQueryHandler.Cap"/>.</summary>
internal sealed record GetUnreadCountQuery : IQuery<UnreadCountResponse>;
