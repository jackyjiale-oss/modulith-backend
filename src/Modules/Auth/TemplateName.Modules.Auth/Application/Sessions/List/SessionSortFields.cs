using TemplateName.Application.Common.Pagination;

namespace TemplateName.Modules.Auth.Application.Sessions.List;

/// <summary>
/// The sort allow-list of the session list. Each allowed field has an index ending in <c>(UserId, Column, Id)</c>
/// (<c>UserSessionConfiguration</c>, review P7), because the list always filters on the owner.
/// </summary>
internal static class SessionSortFields
{
    public const string Default = "-lastSeenAt";

    public static readonly SortField LastSeenAt = new("lastSeenAt", "[LastSeenAt]", typeof(DateTime));

    public static readonly SortField CreatedAt = new("createdAt", "[CreatedAt]", typeof(DateTime));

    public static readonly SortField Id = new("id", "[Id]", typeof(Guid));

    public static readonly IReadOnlyList<SortField> Allowed = [LastSeenAt, CreatedAt];

    /// <summary>The value of <paramref name="field"/> in <paramref name="item"/>, as read from the database.</summary>
    public static object ValueOf(SessionResponse item, SortField field)
    {
        if (field == LastSeenAt)
        {
            return item.LastSeenAt;
        }

        if (field == CreatedAt)
        {
            return item.CreatedAt;
        }

        return field == Id ? item.Id : throw new ArgumentOutOfRangeException(nameof(field), field.Name, "Not a session sort field.");
    }
}
