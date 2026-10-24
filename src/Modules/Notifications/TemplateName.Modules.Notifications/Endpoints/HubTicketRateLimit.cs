using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using TemplateName.Modules.Notifications.Application.HubTickets;
using TemplateName.Web.Common.Security;

namespace TemplateName.Modules.Notifications.Endpoints;

/// <summary>
/// The rate-limit policy of <c>POST /api/v1/notifications/hub-tickets</c>: a fixed window of one minute per signed-in user
/// (<c>user:{sub}</c>; per client address for a request without one, which the endpoint refuses anyway) allowing
/// <see cref="HubOptions.TicketsPerMinute"/> tickets. It applies on top of the global limiter and answers with the host's usual 429
/// <c>rate_limit.exceeded</c> problem. The window comes from the host's <see cref="IFixedWindowLimiterFactory"/>: counted in memory
/// per instance by default, shared by every instance through Redis with <c>Caching:Provider = redis</c>. Every ticket is a row until
/// the hub ticket cleanup job deletes it (an hour after it expires, by default), so this bounds what one account can add.
/// </summary>
internal static class HubTicketRateLimit
{
    public const string PolicyName = "notifications-hub-tickets";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static void AddPolicy(RateLimiterOptions limiterOptions, HubOptions hubOptions, IFixedWindowLimiterFactory limiterFactory)
        => limiterOptions.AddPolicy(PolicyName, httpContext => RateLimitPartition.Get(
            PartitionKey(httpContext),
            partitionKey => limiterFactory.Create(PolicyName, partitionKey, hubOptions.TicketsPerMinute, Window)));

    private static string PartitionKey(HttpContext httpContext)
    {
        var user = httpContext.User;
        var subject = user.Identity?.IsAuthenticated == true ? user.FindFirst("sub")?.Value : null;

        return subject is not null ? $"user:{subject}" : $"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }
}
