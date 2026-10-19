using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace TemplateName.Modules.Notifications.Infrastructure.Hubs;

/// <summary>The <see cref="SchemeName"/> authentication scheme (<see cref="HubTicketAuthenticationHandler"/>).</summary>
internal sealed class HubTicketAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>The scheme's name, which the hub's authorization policy lists next to the JWT bearer scheme.</summary>
    public const string SchemeName = "HubTicket";

    /// <summary>The query parameter in which SignalR clients that cannot set headers (browsers) send the ticket.</summary>
    public const string QueryParameterName = "access_token";

    /// <summary>The only path, with everything below it, on which a ticket is read: the hub's negotiate and connection requests.</summary>
    public PathString PathPrefix { get; set; } = NotificationsHub.Path;
}
