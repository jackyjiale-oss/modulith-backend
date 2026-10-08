using TemplateName.Application.Common.Pagination;

namespace TemplateName.Modules.Auth.Application.Admin.Permissions.List;

/// <summary>
/// The sort allow-list of the permission list: only <c>code</c>, which is unique (<c>IX_Permissions_Code</c> serves it), so the id
/// tie-breaker never decides an order. The table holds one row per declared permission, so no further index is needed.
/// </summary>
internal static class PermissionSortFields
{
    public const string Default = "code";

    public static readonly SortField Code = new("code", "[Code]", typeof(string));

    public static readonly SortField Id = new("id", "[Id]", typeof(Guid));

    public static readonly IReadOnlyList<SortField> Allowed = [Code];

    /// <summary>The value of <paramref name="field"/> in <paramref name="item"/>, as stored in its column.</summary>
    public static object ValueOf(PermissionResponse item, SortField field)
    {
        if (field == Code)
        {
            return item.Code;
        }

        return field == Id ? item.Id : throw new ArgumentOutOfRangeException(nameof(field), field.Name, "Not a permission sort field.");
    }
}
