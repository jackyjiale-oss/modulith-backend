using TemplateName.Application.Common.Pagination;
using TemplateName.Modules.Auth.Domain.Roles;

namespace TemplateName.Modules.Auth.Application.Admin.Roles.List;

/// <summary>
/// The sort allow-list of the role list. <c>name</c> sorts by <c>NormalizedName</c>, whose filtered unique index
/// (<c>IX_Roles_NormalizedName</c>, <c>IsDeleted = 0</c>) serves it; <c>createdAt</c> has the filtered index <c>(CreatedAt, Id)</c>
/// (review P7).
/// </summary>
internal static class RoleSortFields
{
    public const string Default = "name";

    public static readonly SortField Name = new("name", "[NormalizedName]", typeof(string));

    public static readonly SortField CreatedAt = new("createdAt", "[CreatedAt]", typeof(DateTime));

    public static readonly SortField Id = new("id", "[Id]", typeof(Guid));

    public static readonly IReadOnlyList<SortField> Allowed = [Name, CreatedAt];

    /// <summary>The value of <paramref name="field"/> in <paramref name="item"/>, as stored in its column.</summary>
    public static object ValueOf(RoleListItemResponse item, SortField field)
    {
        // The column holds Role.NormalizeName of the stored (trimmed) name, so this is exactly the column's value.
        if (field == Name)
        {
            return Role.NormalizeName(item.Name);
        }

        if (field == CreatedAt)
        {
            return item.CreatedAt;
        }

        return field == Id ? item.Id : throw new ArgumentOutOfRangeException(nameof(field), field.Name, "Not a role sort field.");
    }
}
