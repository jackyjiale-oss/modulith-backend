namespace TemplateName.Infrastructure.Common.Outbox;

/// <summary>A domain event saved with the aggregate that raised it, waiting to be dispatched to its handlers (ADR 0007).</summary>
internal sealed class OutboxMessage
{
    internal const int TypeMaxLength = 500;
    internal const int ErrorMaxLength = 2000;

    public Guid Id { get; set; }

    /// <summary>The event type's <see cref="System.Type.FullName"/>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The event serialized as JSON.</summary>
    public string Content { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; }

    /// <summary>When every handler had succeeded; <see langword="null"/> while the message is pending or abandoned.</summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>The number of failed dispatch attempts.</summary>
    public int AttemptCount { get; set; }

    /// <summary>The earliest time of the next attempt after a failure; <see langword="null"/> when due now or abandoned.</summary>
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>The end of the lease held by the dispatcher that claimed the message.</summary>
    public DateTime? LockedUntil { get; set; }

    /// <summary>The last failure, truncated to <see cref="ErrorMaxLength"/> characters.</summary>
    public string? Error { get; set; }
}
