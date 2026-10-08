namespace TemplateName.Application.Common.Pagination;

/// <summary>
/// A decoded cursor: the direction to page in, the sort and filters it was issued for, and the key values of the row it points at
/// (one per sort term, in term order, the tie-breaking id last).
/// </summary>
public sealed record Cursor(CursorDirection Direction, string SortSignature, string FilterHash, IReadOnlyList<object> KeyValues);
