using TemplateName.Application.Common.Pagination;

namespace TemplateName.Modules.Sample.Application.LeaveRequests.List;

/// <summary>
/// The sort allow-list of the leave request list. Each allowed field has an index ending in <c>(…, Column, Id)</c>
/// (<c>LeaveRequestConfiguration</c>, review P7).
/// </summary>
internal static class LeaveRequestSortFields
{
    public const string Default = "-createdAt";

    public static readonly SortField CreatedAt = new("createdAt", "[CreatedAt]", typeof(DateTime));

    public static readonly SortField StartDate = new("startDate", "[StartDate]", typeof(DateOnly));

    public static readonly SortField Id = new("id", "[Id]", typeof(Guid));

    public static readonly IReadOnlyList<SortField> Allowed = [CreatedAt, StartDate];

    /// <summary>The value of <paramref name="field"/> in <paramref name="item"/>, as read from the database.</summary>
    public static object ValueOf(LeaveRequestListItemResponse item, SortField field)
    {
        if (field == CreatedAt)
        {
            return item.CreatedAt;
        }

        if (field == StartDate)
        {
            return item.StartDate;
        }

        return field == Id ? item.Id : throw new ArgumentOutOfRangeException(nameof(field), field.Name, "Not a leave request sort field.");
    }
}
