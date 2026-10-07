namespace TemplateName.Application.Common.Pagination;

/// <summary>Turns the rows a <see cref="KeysetQuery"/> fetched into a <see cref="CursorPage{T}"/> with its cursors.</summary>
public static class CursorPageBuilder
{
    /// <summary>
    /// Drops the extra row (the query fetched page size + 1) and restores the requested order of a backward page. Forward:
    /// <c>nextCursor</c> is set when the extra row existed and <c>previousCursor</c> when a cursor was supplied. Backward:
    /// <c>previousCursor</c> is set when the extra row existed and <c>nextCursor</c> always. An empty page has no cursors, since there
    /// is no row to point at. <paramref name="keySelector"/> returns a row's values for the sort terms, in term order, exactly as
    /// stored (read from the row, never recomputed), so the next comparison matches the database.
    /// </summary>
    public static CursorPage<T> Build<T>(
        IReadOnlyList<T> fetchedRows,
        KeysetQuery query,
        Cursor? incomingCursor,
        SortSpecification sort,
        string filterHash,
        Func<T, IReadOnlyList<object>> keySelector,
        long? totalCount)
    {
        var pageSize = query.Take - 1;
        var hasMore = fetchedRows.Count > pageSize;
        var items = fetchedRows.Take(pageSize).ToList();
        if (query.IsBackward)
        {
            items.Reverse();
        }

        string? nextCursor = null;
        string? previousCursor = null;
        if (items.Count > 0)
        {
            var isNextAvailable = query.IsBackward || hasMore;
            var isPreviousAvailable = query.IsBackward ? hasMore : incomingCursor is not null;
            nextCursor = isNextAvailable ? Encode(CursorDirection.Next, items[^1]) : null;
            previousCursor = isPreviousAvailable ? Encode(CursorDirection.Previous, items[0]) : null;
        }

        return new CursorPage<T>(items, pageSize, nextCursor, previousCursor, totalCount);

        string Encode(CursorDirection direction, T row)
            => CursorCodec.Encode(new Cursor(direction, sort.Signature, filterHash, keySelector(row)));
    }
}
