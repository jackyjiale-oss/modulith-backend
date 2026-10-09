namespace TemplateName.Infrastructure.Common.Inbox;

/// <summary>
/// Records that one consumer has processed one integration event (per-consumer idempotency, ADR 0018). The primary key
/// (<see cref="MessageId"/>, <see cref="Consumer"/>) makes a second record of the same pair fail, even from a concurrent save.
/// </summary>
public sealed class InboxMessage
{
    public const int ConsumerMaxLength = 500;

    /// <summary>The name of the primary key constraint; <see cref="Inbox{TContext}.IsDuplicate"/> looks for it in SQL Server's error.</summary>
    internal const string PrimaryKeyName = "PK_InboxMessages";

    /// <summary>The integration event's <c>Id</c> (the publisher's outbox message id).</summary>
    public Guid MessageId { get; set; }

    /// <summary>The consumer's stable name, by convention its handler type's <see cref="Type.FullName"/>.</summary>
    public string Consumer { get; set; } = string.Empty;

    public DateTime ProcessedAt { get; set; }
}
