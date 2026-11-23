using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using TemplateName.Modules.Notifications.Application.Devices;
using TemplateName.Web.Common.Security;

namespace TemplateName.Modules.Notifications.Endpoints;

/// <summary>
/// The rate-limit policy of <c>POST /api/v1/notifications/devices</c>: at most <c>Notifications:Push:RegistrationsPerMinute</c>
/// registrations per user per minute (a signed-in caller is partitioned by its <c>sub</c> claim, as the hub ticket route is). Each
/// registration writes a row, so the limit bounds what one account can store between cleanups.
/// </summary>
internal static class PushDeviceRateLimit
{
    public const string PolicyName = "notifications-push-devices";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static void AddPolicy(RateLimiterOptions limiterOptions, PushOptions pushOptions, IFixedWindowLimiterFactory limiterFactory)
        => limiterOptions.AddPolicy(PolicyName, httpContext => RateLimitPartition.Get(
            PartitionKey(httpContext),
            partitionKey => limiterFactory.Create(PolicyName, partitionKey, pushOptions.RegistrationsPerMinute, Window)));

    private static string PartitionKey(HttpContext httpContext)
    {
        var user = httpContext.User;
        var subject = user.Identity?.IsAuthenticated == true ? user.FindFirst("sub")?.Value : null;
        return subject is not null ? $"user:{subject}" : $"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }
}
