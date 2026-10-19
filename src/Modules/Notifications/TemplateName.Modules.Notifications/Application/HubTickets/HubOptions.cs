using System.ComponentModel.DataAnnotations;

namespace TemplateName.Modules.Notifications.Application.HubTickets;

/// <summary>The notification hub and its tickets (section <c>Notifications:Hub</c>), validated on start (D12, ADR 0021).</summary>
internal sealed class HubOptions
{
    internal const string SectionName = "Notifications:Hub";

    /// <summary>How long an issued ticket can be used. A client fetches one right before each request that opens a connection.</summary>
    [Range(typeof(TimeSpan), "00:00:05", "00:05:00")]
    public TimeSpan TicketLifetime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a connection opened with a ticket stays open: its authentication expires then, the hub closes it and the client
    /// reconnects with new tickets.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "01:00:00")]
    public TimeSpan ConnectionLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The most tickets one user may get per minute (a fixed window). Opening a connection takes two; the rest covers reconnects and
    /// several tabs. A ticket row stays until the cleanup job of Plan 5, so this bounds what one account can add to the table.
    /// </summary>
    [Range(1, 1000)]
    public int TicketsPerMinute { get; set; } = 30;
}
