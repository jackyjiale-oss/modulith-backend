using TemplateName.Application.Common.Pagination;

namespace TemplateName.UnitTests.Pagination;

public sealed class CursorPageBuilderTests
{
    private const int PageSize = 3;
    private const string FilterHash = "hash";

    private static readonly SortField Number = new("number", "[Number]", typeof(int));
    private static readonly SortField IdField = new("id", "[Id]", typeof(Guid));
    private static readonly SortSpecification Ascending = SortSpecification.Parse("number", [Number], IdField, "number").Value;

    [Fact]
    public void First_page_with_an_extra_row_trims_it_and_points_next_at_the_last_item()
    {
        var rows = Rows(1, 2, 3, 4);

        var page = Build(rows, cursor: null);

        page.Items.ShouldBe(rows.Take(PageSize));
        page.PageSize.ShouldBe(PageSize);
        page.PreviousCursor.ShouldBeNull();
        Decode(page.NextCursor!).ShouldBe((CursorDirection.Next, 3, rows[2].Id));
    }

    [Fact]
    public void Last_forward_page_has_no_next_cursor_but_a_previous_one_at_the_first_item()
    {
        var rows = Rows(7, 8);

        var page = Build(rows, Incoming(CursorDirection.Next));

        page.Items.ShouldBe(rows);
        page.NextCursor.ShouldBeNull();
        Decode(page.PreviousCursor!).ShouldBe((CursorDirection.Previous, 7, rows[0].Id));
    }

    [Fact]
    public void Backward_page_is_re_reversed_and_always_has_a_next_cursor()
    {
        // A backward query returns rows in reverse order: 6, 5, 4, then the extra row 3.
        var rows = Rows(6, 5, 4, 3);

        var page = Build(rows, Incoming(CursorDirection.Previous));

        page.Items.Select(row => row.Number).ShouldBe([4, 5, 6]);
        Decode(page.PreviousCursor!).ShouldBe((CursorDirection.Previous, 4, rows[2].Id));
        Decode(page.NextCursor!).ShouldBe((CursorDirection.Next, 6, rows[0].Id));
    }

    [Fact]
    public void Backward_page_without_an_extra_row_is_the_first_page()
    {
        var page = Build(Rows(2, 1), Incoming(CursorDirection.Previous));

        page.Items.Select(row => row.Number).ShouldBe([1, 2]);
        page.PreviousCursor.ShouldBeNull();
        page.NextCursor.ShouldNotBeNull();
    }

    [Fact]
    public void Empty_page_keeps_the_way_back_to_the_incoming_cursor_position_and_the_total_count()
    {
        // No cursor: an empty list has nowhere to go.
        var first = Build([], cursor: null, totalCount: 0);
        first.Items.ShouldBeEmpty();
        first.NextCursor.ShouldBeNull();
        first.PreviousCursor.ShouldBeNull();
        first.TotalCount.ShouldBe(0);

        // Forward past the last row: nothing further, but the client can page back from the incoming position.
        var forwardCursor = Incoming(CursorDirection.Next);
        var forward = Build([], forwardCursor, totalCount: 0);
        forward.Items.ShouldBeEmpty();
        forward.NextCursor.ShouldBeNull();
        Decode(forward.PreviousCursor!).ShouldBe((CursorDirection.Previous, 5, (Guid)forwardCursor.KeyValues[1]));
        forward.TotalCount.ShouldBe(0);

        // Backward before the first row: nothing further back, but the client can page forward from the incoming position.
        var backwardCursor = Incoming(CursorDirection.Previous);
        var backward = Build([], backwardCursor);
        backward.Items.ShouldBeEmpty();
        backward.PreviousCursor.ShouldBeNull();
        Decode(backward.NextCursor!).ShouldBe((CursorDirection.Next, 5, (Guid)backwardCursor.KeyValues[1]));
    }

    private static CursorPage<Row> Build(IReadOnlyList<Row> rows, Cursor? cursor, long? totalCount = null)
        => CursorPageBuilder.Build(
            rows,
            KeysetSqlBuilder.Build(Ascending, cursor, PageSize),
            cursor,
            Ascending,
            FilterHash,
            row => [row.Number, row.Id],
            totalCount);

    private static Cursor Incoming(CursorDirection direction) => new(direction, Ascending.Signature, FilterHash, [5, Guid.NewGuid()]);

    private static (CursorDirection Direction, int Number, Guid Id) Decode(string token)
    {
        var cursor = CursorCodec.Decode(token, Ascending, FilterHash).Value;
        return (cursor.Direction, (int)cursor.KeyValues[0], (Guid)cursor.KeyValues[1]);
    }

    private static List<Row> Rows(params int[] numbers) => [.. numbers.Select(number => new Row(number, Guid.NewGuid()))];

    private sealed record Row(int Number, Guid Id);
}
