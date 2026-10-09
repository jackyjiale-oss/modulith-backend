namespace TemplateName.Modules.Notifications.Domain.Deliveries;

/// <summary>
/// When a failed delivery is tried again (blueprint 10.4): 1 minute, 5 minutes, 15 minutes, 1 hour and 6 hours after failed
/// attempts 1 to 5, and 6 hours after any later one. Pure: the caller passes the clock.
/// </summary>
internal static class DeliveryRetryPolicy
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
    ];

    /// <summary>
    /// The time of the next attempt after <paramref name="failedAttempts"/> failures, or <see langword="null"/> once
    /// <paramref name="failedAttempts"/> has reached <paramref name="maxAttempts"/> and the delivery is to be dead-lettered.
    /// </summary>
    /// <param name="failedAttempts">The number of attempts that have failed so far, at least 1.</param>
    /// <param name="utcNow">The current UTC time; the result has the same <see cref="DateTimeKind"/>.</param>
    /// <param name="maxAttempts">The attempts allowed in total, at least 1.</param>
    public static DateTime? NextAttemptAt(int failedAttempts, DateTime utcNow, int maxAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        if (failedAttempts >= maxAttempts)
        {
            return null;
        }

        return utcNow + Delays[Math.Min(failedAttempts, Delays.Length) - 1];
    }
}
