using System.Text.Json.Serialization;

namespace TemplateName.Application.Common.Pagination;

/// <summary>
/// One page of a list. A cursor is <see langword="null"/> when there is no page in that direction; <see cref="TotalCount"/> is
/// written only when the caller asked for it.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
public sealed record CursorPage<T>(
    IReadOnlyList<T> Items,
    int PageSize,
    string? NextCursor,
    string? PreviousCursor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TotalCount);
