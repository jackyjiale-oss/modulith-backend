namespace TemplateName.Modules.Notifications.Application.Inbox.UnreadCount;

/// <summary>The unread badge of the inbox.</summary>
/// <param name="Count">The number of unread notifications, at most 1000.</param>
/// <param name="IsCapped">
/// True when the count stopped at the cap: there are at least that many unread. Exactly 1000 unread is reported as capped too, because
/// the count does not look past the cap, so it cannot tell 1000 from more. Show it as "1000+".
/// </param>
internal sealed record UnreadCountResponse(int Count, bool IsCapped);
