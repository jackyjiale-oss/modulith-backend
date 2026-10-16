using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using TemplateName.Modules.Notifications.Application.HubTickets;

namespace TemplateName.Modules.Notifications.Endpoints;

/// <summary>
/// The rate-limit policy of <c>POST /api/v1/notifications/hub-tickets</c>: a fixed window of one minute per signed-in user
/// (<c>user:{sub}</c>; per client address for a request without one, which the endpoint refuses anyway) allowing
/// <see cref="HubOptions.TicketsPerMinute"/> tickets. It applies on top of the global limiter and answers with the host's usual 429
/// <c>rate_limit.exceeded</c> problem. Every ticket is a row until Plan 5's cleanup job, so this bounds what one account can add.
/// </summary>
internal static class HubTicketRateLimit
{
    public const string PolicyName = "notifications-hub-tickets";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static void AddPolicy(RateLimiterOptions limiterOptions, HubOptions hubOptions)
        => limiterOptions.AddPolicy(PolicyName, httpContext => RateLimitPartition.GetFixedWindowLimiter(
            PartitionKey(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = hubOptions.TicketsPerMinute,
                Window = Window,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    private static string PartitionKey(HttpContext httpContext)
    {
        var user = httpContext.User;
        var subject = user.Identity?.IsAuthenticated == true ? user.FindFirst("sub")?.Value : null;

        return subject is not null ? $"user:{subject}" : $"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }
}
