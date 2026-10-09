namespace TemplateName.Infrastructure.Common.Inbox;

/// <summary>
/// Records that one consumer has processed one integration event (per-consumer idempotency, ADR 0018). The primary key
/// (<see cref="MessageId"/>, <see cref="Consumer"/>) makes a second record of the same pair fail, even from a concurrent save.
/// </summary>
public sealed class InboxMessage
{
    /// <summary>
    /// The longest consumer name. The clustered primary key is <c>uniqueidentifier</c> (16 bytes) plus <c>nvarchar(400)</c> (800
    /// bytes), under SQL Server's 900-byte limit for a clustered index key; a longer column would make long names fail on insert.
    /// </summary>
    public const int ConsumerMaxLength = 400;

    /// <summary>The name of the primary key constraint; <see cref="Inbox{TContext}.IsDuplicate"/> looks for it in SQL Server's error.</summary>
    internal const string PrimaryKeyName = "PK_InboxMessages";

    /// <summary>The integration event's <c>Id</c> (the publisher's outbox message id).</summary>
    public Guid MessageId { get; set; }

    /// <summary>The consumer's stable name, by convention its handler type's <see cref="Type.FullName"/>.</summary>
    public string Consumer { get; set; } = string.Empty;

    public DateTime ProcessedAt { get; set; }
}
