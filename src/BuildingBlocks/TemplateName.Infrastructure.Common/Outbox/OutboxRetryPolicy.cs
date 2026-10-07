namespace TemplateName.Infrastructure.Common.Outbox;

/// <summary>When a failed outbox message is tried again.</summary>
public static class OutboxRetryPolicy
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
    ];

    /// <summary>
    /// Returns the time of the next attempt after <paramref name="attemptCount"/> failed attempts: 5 s, 30 s, 2 min and 10 min after
    /// attempts 1 to 4, and 1 h after every later one. Returns <see langword="null"/> once <paramref name="maxAttempts"/> is reached,
    /// which abandons the message.
    /// </summary>
    public static DateTime? NextAttemptAt(int attemptCount, DateTime utcNow, int maxAttempts)
    {
        if (attemptCount >= maxAttempts)
        {
            return null;
        }

        var delay = Delays[Math.Clamp(attemptCount, 1, Delays.Length) - 1];
        return utcNow + delay;
    }
}
