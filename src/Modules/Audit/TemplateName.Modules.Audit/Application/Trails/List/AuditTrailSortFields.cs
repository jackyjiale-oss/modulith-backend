using TemplateName.Application.Common.Pagination;

namespace TemplateName.Modules.Audit.Application.Trails.List;

/// <summary>
/// The sort allow-list of the audit trail: only <c>occurredAt</c>, with the line's <c>bigint</c> <c>Id</c> as the unique tie-breaker
/// (lines written at the same instant keep their insertion order). The indexes <c>(OccurredAt, Id)</c>,
/// <c>(EntityType, EntityId, OccurredAt, Id)</c> and <c>(UserId, OccurredAt, Id)</c> serve the unfiltered list, the history of one record
/// and what one person changed in either direction (ADR 0010); the other filters are checked on the rows those indexes return.
/// </summary>
internal static class AuditTrailSortFields
{
    public const string Default = "-occurredAt";

    public static readonly SortField OccurredAt = new("occurredAt", "[OccurredAt]", typeof(DateTime));

    public static readonly SortField Id = new("id", "[Id]", typeof(long));

    public static readonly IReadOnlyList<SortField> Allowed = [OccurredAt];

    /// <summary>The value of <paramref name="field"/> in <paramref name="item"/>, as stored in its column.</summary>
    public static object ValueOf(AuditTrailResponse item, SortField field)
    {
        if (field == OccurredAt)
        {
            return item.OccurredAt;
        }

        return field == Id ? item.Id : throw new ArgumentOutOfRangeException(nameof(field), field.Name, "Not an audit trail sort field.");
    }
}
