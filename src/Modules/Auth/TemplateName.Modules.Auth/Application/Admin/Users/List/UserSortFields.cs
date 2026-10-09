using TemplateName.Application.Common.Pagination;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Application.Admin.Users.List;

/// <summary>
/// The sort allow-list of the user list. <c>email</c> sorts by <c>NormalizedEmail</c>, whose filtered unique index (<c>IsDeleted = 0</c>)
/// also serves the prefix search; <c>createdAt</c> has the filtered index <c>(CreatedAt, Id)</c> (review P7).
/// </summary>
internal static class UserSortFields
{
    public const string Default = "-createdAt";

    public static readonly SortField CreatedAt = new("createdAt", "[CreatedAt]", typeof(DateTime));

    public static readonly SortField Email = new("email", "[NormalizedEmail]", typeof(string));

    public static readonly SortField Id = new("id", "[Id]", typeof(Guid));

    public static readonly IReadOnlyList<SortField> Allowed = [CreatedAt, Email];

    /// <summary>The value of <paramref name="field"/> in <paramref name="item"/>, as stored in its column.</summary>
    public static object ValueOf(UserListItemResponse item, SortField field)
    {
        if (field == CreatedAt)
        {
            return item.CreatedAt;
        }

        // The column holds User.NormalizeEmail of the stored (trimmed) Email, so this is exactly the column's value.
        if (field == Email)
        {
            return User.NormalizeEmail(item.Email);
        }

        return field == Id ? item.Id : throw new ArgumentOutOfRangeException(nameof(field), field.Name, "Not a user sort field.");
    }
}
