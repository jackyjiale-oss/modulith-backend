namespace TemplateName.Application.Common.Pagination;

/// <summary>Turns the rows a <see cref="KeysetQuery"/> fetched into a <see cref="CursorPage{T}"/> with its cursors.</summary>
public static class CursorPageBuilder
{
    /// <summary>
    /// Drops the extra row (the query fetched page size + 1) and restores the requested order of a backward page. Forward:
    /// <c>nextCursor</c> is set when the extra row existed and <c>previousCursor</c> when a cursor was supplied. Backward:
    /// <c>previousCursor</c> is set when the extra row existed and <c>nextCursor</c> always. An empty page reached with a cursor (the
    /// rows beyond it were deleted or left the filter) points back the way it came from the incoming cursor's position: forward pages
    /// get a <c>previousCursor</c>, backward pages a <c>nextCursor</c>; the row at that position is not shown again. An empty first page
    /// has no cursors. <paramref name="keySelector"/> returns a row's values for the sort terms, in term order, exactly as
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
            nextCursor = isNextAvailable ? Encode(CursorDirection.Next, keySelector(items[^1])) : null;
            previousCursor = isPreviousAvailable ? Encode(CursorDirection.Previous, keySelector(items[0])) : null;
        }
        else if (incomingCursor is not null)
        {
            // Nothing lies beyond the incoming position any more (rows were deleted or left the filter): keep the way back from it.
            nextCursor = query.IsBackward ? Encode(CursorDirection.Next, incomingCursor.KeyValues) : null;
            previousCursor = query.IsBackward ? null : Encode(CursorDirection.Previous, incomingCursor.KeyValues);
        }

        return new CursorPage<T>(items, pageSize, nextCursor, previousCursor, totalCount);

        string Encode(CursorDirection direction, IReadOnlyList<object> keyValues)
            => CursorCodec.Encode(new Cursor(direction, sort.Signature, filterHash, keyValues));
    }
}
