using TemplateName.Application.Common.Pagination;

namespace TemplateName.Modules.Auth.Application.Admin.AuditLogs.List;

/// <summary>
/// The sort allow-list of the audit log: only <c>occurredAt</c>, with the entry's <c>bigint</c> <c>Id</c> as the unique tie-breaker
/// (entries written at the same instant keep their insertion order). Indexes <c>(OccurredAt, Id)</c>, <c>(UserId, OccurredAt, Id)</c> and
/// <c>(EventType, OccurredAt, Id)</c> serve the unfiltered list and the <c>userId</c> and <c>eventType</c> filters in either direction
/// (review P7); <c>succeeded</c> and the dates are checked on the rows those indexes return.
/// </summary>
internal static class AuditLogSortFields
{
    public const string Default = "-occurredAt";

    public static readonly SortField OccurredAt = new("occurredAt", "[OccurredAt]", typeof(DateTime));

    public static readonly SortField Id = new("id", "[Id]", typeof(long));

    public static readonly IReadOnlyList<SortField> Allowed = [OccurredAt];

    /// <summary>The value of <paramref name="field"/> in <paramref name="item"/>, as stored in its column.</summary>
    public static object ValueOf(AuditLogResponse item, SortField field)
    {
        if (field == OccurredAt)
        {
            return item.OccurredAt;
        }

        return field == Id ? item.Id : throw new ArgumentOutOfRangeException(nameof(field), field.Name, "Not an audit log sort field.");
    }
}
