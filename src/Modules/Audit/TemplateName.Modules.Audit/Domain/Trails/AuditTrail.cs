using TemplateName.SharedKernel;
using TemplateName.SharedKernel.Auditing;

namespace TemplateName.Modules.Audit.Domain.Trails;

/// <summary>
/// One line of the append-only audit trail: the change one save made to one audited entity of another module. It is not an aggregate:
/// it has no domain events and its key is a database identity. It is never audited itself.
/// </summary>
internal sealed class AuditTrail : Entity<long>
{
    private const string Ellipsis = "…";

    /// <summary>The column limit of <see cref="Module"/>.</summary>
    public const int MaxModuleLength = 64;

    /// <summary>The column limit of <see cref="EntityType"/>.</summary>
    public const int MaxEntityTypeLength = 128;

    /// <summary>The column limit of <see cref="EntityId"/>.</summary>
    public const int MaxEntityIdLength = 200;

    /// <summary>The column limit of <see cref="Action"/>, which holds the <see cref="EntityChangeAction"/> name.</summary>
    public const int MaxActionLength = 16;

    /// <summary>The column limit of <see cref="TraceId"/>.</summary>
    public const int MaxTraceIdLength = 64;

    // EF Core materializes the entity through this constructor; callers use Create.
    private AuditTrail()
    {
    }

    /// <summary>The change set (the integration event id) this row belongs to; every row of one save shares it.</summary>
    public Guid ChangeSetId { get; private set; }

    /// <summary>When the save happened (UTC).</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    public Guid? UserId { get; private set; }

    public Guid? SessionId { get; private set; }

    /// <summary>The owning module (the saved context's schema, for example <c>sample</c>).</summary>
    public string Module { get; private set; } = string.Empty;

    /// <summary>The CLR type name of the entity without its namespace.</summary>
    public string EntityType { get; private set; } = string.Empty;

    /// <summary>The key of the entity as text; a composite key is <c>Name=value;Name=value</c>.</summary>
    public string EntityId { get; private set; } = string.Empty;

    public EntityChangeAction Action { get; private set; }

    /// <summary>JSON object text keyed by property name, as the owning module recorded it.</summary>
    public string Changes { get; private set; } = string.Empty;

    public string? TraceId { get; private set; }

    public static AuditTrail Create(
        Guid changeSetId,
        DateTimeOffset occurredAt,
        Guid? userId,
        Guid? sessionId,
        string module,
        EntityChange change,
        string? traceId)
    {
        ArgumentNullException.ThrowIfNull(change);

        return new AuditTrail
        {
            ChangeSetId = changeSetId,
            OccurredAt = occurredAt,
            UserId = userId,
            SessionId = sessionId,
            Module = Clamp(module, MaxModuleLength),
            EntityType = Clamp(change.EntityType, MaxEntityTypeLength),
            EntityId = Clamp(change.EntityId, MaxEntityIdLength),
            Action = change.Action,
            Changes = change.Changes,
            TraceId = traceId is null ? null : Clamp(traceId, MaxTraceIdLength),
        };
    }

    /// <summary>
    /// Cuts a value to its column limit, ending in <c>…</c> so a cut value shows as cut. An over-long value would otherwise fail the
    /// save, and the owning module's outbox would retry the whole change set until it was abandoned; one odd key must not cost the
    /// trail of every other change in its save.
    /// </summary>
    private static string Clamp(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var keptLength = maxLength - Ellipsis.Length;

        // Do not leave half of a surrogate pair at the cut.
        if (char.IsHighSurrogate(value[keptLength - 1]))
        {
            keptLength--;
        }

        return string.Concat(value.AsSpan(0, keptLength), Ellipsis);
    }
}
