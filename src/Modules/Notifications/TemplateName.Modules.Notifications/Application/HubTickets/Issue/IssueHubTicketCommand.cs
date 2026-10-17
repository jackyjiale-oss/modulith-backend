using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Notifications.Application.HubTickets.Issue;

/// <summary>Issues the caller a single-use ticket for opening one request to the notification hub (D12, ADR 0021).</summary>
internal sealed record IssueHubTicketCommand : ICommand<HubTicketResponse>;
