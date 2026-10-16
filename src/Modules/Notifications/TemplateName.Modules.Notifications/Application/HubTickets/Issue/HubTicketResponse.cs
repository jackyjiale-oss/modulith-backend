namespace TemplateName.Modules.Notifications.Application.HubTickets.Issue;

/// <summary>A hub ticket: the bearer secret to send once, as <c>access_token</c> or <c>Authorization: Bearer</c>, before <paramref name="ExpiresAt"/>.</summary>
/// <param name="Ticket">43 characters of base64url; valid for one request to <c>/hubs/notifications</c>.</param>
/// <param name="ExpiresAt">When the ticket stops working, used or not (UTC).</param>
internal sealed record HubTicketResponse(string Ticket, DateTimeOffset ExpiresAt);
