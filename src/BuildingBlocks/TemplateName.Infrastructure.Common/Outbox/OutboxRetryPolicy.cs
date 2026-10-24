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

        return utcNow + DelayAfter(attemptCount);
    }

    /// <summary>
    /// How long a message is retried when it is tried up to <paramref name="maxAttempts"/> times: the sum of the delays after attempts 1 to
    /// <paramref name="maxAttempts"/> - 1, so 755 s (about 12.6 min) for the default 5, and one more hour for each attempt after the fifth.
    /// An inbox row must be kept longer than this (<c>Retention:Inbox</c>, ADR 0028).
    /// </summary>
    public static TimeSpan RetryWindow(int maxAttempts)
    {
        var window = TimeSpan.Zero;
        for (var attemptCount = 1; attemptCount < maxAttempts; attemptCount++)
        {
            window += DelayAfter(attemptCount);
        }

        return window;
    }

    private static TimeSpan DelayAfter(int attemptCount) => Delays[Math.Clamp(attemptCount, 1, Delays.Length) - 1];
}
