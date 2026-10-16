using TemplateName.Application.Common.Pagination;

namespace TemplateName.Modules.Notifications.Application.Inbox.List;

/// <summary>
/// The sort allow-list of the inbox: <c>createdAt</c> only, with the unique <c>Id</c> as tie-breaker (several notifications can share
/// an instant). It follows <c>IX_InAppNotifications_UserId_CreatedAt_Id</c> (<c>UserId</c>, <c>CreatedAt DESC</c>, <c>Id DESC</c>),
/// because the list always filters on the owner (review P7); the other direction reads the same index backwards.
/// </summary>
internal static class InAppNotificationSortFields
{
    public const string Default = "-createdAt";

    public static readonly SortField CreatedAt = new("createdAt", "[CreatedAt]", typeof(DateTime));

    public static readonly SortField Id = new("id", "[Id]", typeof(Guid));

    public static readonly IReadOnlyList<SortField> Allowed = [CreatedAt];

    /// <summary>The value of <paramref name="field"/> in <paramref name="item"/>, as read from the database.</summary>
    public static object ValueOf(InAppNotificationResponse item, SortField field)
    {
        if (field == CreatedAt)
        {
            return item.CreatedAt;
        }

        return field == Id ? item.Id : throw new ArgumentOutOfRangeException(nameof(field), field.Name, "Not an inbox sort field.");
    }
}
