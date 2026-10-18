using TemplateName.Application.Common.Pagination;

namespace TemplateName.Modules.Auth.Application.TrustedDevices.List;

/// <summary>
/// The sort allow-list of the trusted device list: <c>createdAt</c> only, newest first by default, with <c>Id</c> as the tie-breaker.
/// The index <c>(UserId, CreatedAt, Id)</c> (<c>TrustedDeviceConfiguration</c>) serves it, because the list always filters on the owner.
/// </summary>
internal static class TrustedDeviceSortFields
{
    public const string Default = "-createdAt";

    public static readonly SortField CreatedAt = new("createdAt", "[CreatedAt]", typeof(DateTime));

    public static readonly SortField Id = new("id", "[Id]", typeof(Guid));

    public static readonly IReadOnlyList<SortField> Allowed = [CreatedAt];

    /// <summary>The value of <paramref name="field"/> in <paramref name="item"/>, as read from the database.</summary>
    public static object ValueOf(TrustedDeviceResponse item, SortField field)
    {
        if (field == CreatedAt)
        {
            return item.CreatedAt;
        }

        return field == Id ? item.Id : throw new ArgumentOutOfRangeException(nameof(field), field.Name, "Not a trusted device sort field.");
    }
}
