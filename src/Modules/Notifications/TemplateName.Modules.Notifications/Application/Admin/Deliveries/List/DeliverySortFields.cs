using TemplateName.Application.Common.Pagination;

namespace TemplateName.Modules.Notifications.Application.Admin.Deliveries.List;

/// <summary>
/// The sort allow-list of the delivery list: <c>createdAt</c> only, with the delivery's unique <c>Id</c> as tie-breaker (deliveries of
/// one notification share an instant). The columns are qualified with the alias <c>d</c> of <see cref="ListDeliveriesQueryHandler"/>,
/// whose query also joins the notification (<c>n</c>). <c>IX_Deliveries_CreatedAt_Id</c> serves the unfiltered list in either
/// direction and <c>IX_Deliveries_Status_CreatedAt_Id</c> the <c>status</c> filter; <c>recipientUserId</c> is found through
/// <c>IX_Notifications_RecipientUserId</c> (a user's notifications are few, so sorting their deliveries is cheap), while <c>channel</c>
/// and <c>typeCode</c> narrow the rows those indexes return (review P7).
/// </summary>
internal static class DeliverySortFields
{
    public const string Default = "-createdAt";

    public static readonly SortField CreatedAt = new("createdAt", "[d].[CreatedAt]", typeof(DateTime));

    public static readonly SortField Id = new("id", "[d].[Id]", typeof(Guid));

    public static readonly IReadOnlyList<SortField> Allowed = [CreatedAt];

    /// <summary>The value of <paramref name="field"/> in <paramref name="item"/>, as read from the database.</summary>
    public static object ValueOf(DeliveryResponse item, SortField field)
    {
        if (field == CreatedAt)
        {
            return item.CreatedAt;
        }

        return field == Id ? item.Id : throw new ArgumentOutOfRangeException(nameof(field), field.Name, "Not a delivery sort field.");
    }
}
