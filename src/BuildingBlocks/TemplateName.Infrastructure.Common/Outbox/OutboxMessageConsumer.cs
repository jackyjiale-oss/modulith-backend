namespace TemplateName.Infrastructure.Common.Outbox;

/// <summary>Records that one handler has processed one outbox message, so a retry skips it (per-handler idempotency, ADR 0007).</summary>
internal sealed class OutboxMessageConsumer
{
    internal const int NameMaxLength = 500;

    public Guid OutboxMessageId { get; set; }

    /// <summary>The handler type's <see cref="Type.FullName"/>.</summary>
    public string Name { get; set; } = string.Empty;

    public DateTime ProcessedAt { get; set; }
}
