using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;

namespace TemplateName.Modules.Notifications.Application.Inbox.List;

/// <summary>A page of the caller's in-app notifications, newest first by default.</summary>
/// <param name="Page">The paging part: page size, cursor, sort (<c>createdAt</c> only) and the opt-in total count.</param>
/// <param name="UnreadOnly">Only the notifications that have not been read. A cursor is valid only with the value it was made with.</param>
internal sealed record ListInboxQuery(CursorPageRequest Page, bool UnreadOnly) : IQuery<CursorPage<InAppNotificationResponse>>;
