using TemplateName.SharedKernel;
using TemplateName.SharedKernel.Auditing;

namespace TemplateName.IntegrationTests.Persistence;

/// <summary>
/// An audited, auditable and soft-deletable entity that exercises the audit trail interceptor. It raises no domain events, so every
/// outbox row its saves write is an audit row. <see cref="RowVersion"/> lets a test cause a concurrency conflict.
/// </summary>
[Audited]
internal sealed class AuditedTestAggregate : IAuditable, ISoftDeletable
{
    private AuditedTestAggregate()
    {
    }

    public Guid Id { get; private set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>A high-frequency value the trail leaves out.</summary>
    [AuditIgnore]
    public int Counter { get; set; }

    public byte[] RowVersion { get; private set; } = [];

    public DateTime CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTime? UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public bool IsDeleted { get; private set; }

    public DateTime? DeletedAt { get; private set; }

    public Guid? DeletedBy { get; private set; }

    public static AuditedTestAggregate Create(string title, DateTimeOffset now) => new() { Id = SequentialGuid.Create(now), Title = title };
}
