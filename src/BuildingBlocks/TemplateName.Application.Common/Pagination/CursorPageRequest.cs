namespace TemplateName.Application.Common.Pagination;

/// <summary>
/// The paging part of a list query (review Section 9.3, ADR 0010): <c>pageSize</c> (1 to <see cref="MaxPageSize"/>), an opaque
/// <c>cursor</c> from a previous page, a <c>sort</c> such as <c>-createdAt</c> and the opt-in <c>includeTotalCount</c>.
/// </summary>
public sealed record CursorPageRequest(
    int PageSize = CursorPageRequest.DefaultPageSize,
    string? Cursor = null,
    string? Sort = null,
    bool IncludeTotalCount = false)
{
    public const int DefaultPageSize = 20;

    public const int MaxPageSize = 100;
}
