using TemplateName.Application.Common.Pagination;

namespace TemplateName.UnitTests.Pagination;

public sealed class KeysetSqlBuilderTests
{
    private const int PageSize = 20;

    private static readonly SortField CreatedAt = new("createdAt", "[CreatedAt]", typeof(DateTime));
    private static readonly SortField StartDate = new("startDate", "[StartDate]", typeof(DateOnly));
    private static readonly SortField IdField = new("id", "[Id]", typeof(Guid));
    private static readonly SortField[] Allowed = [CreatedAt, StartDate];

    private static readonly SortSpecification NewestFirst = SortSpecification.Parse("-createdAt", Allowed, IdField, "-createdAt").Value;
    private static readonly DateTime LastCreatedAt = new(2026, 1, 1, 8, 30, 0, 123, DateTimeKind.Utc);
    private static readonly Guid LastId = Guid.NewGuid();

    [Fact]
    public void No_cursor_produces_true_predicate_and_requested_order()
    {
        var query = KeysetSqlBuilder.Build(NewestFirst, cursor: null, PageSize);

        query.WhereClause.ShouldBe("1 = 1");
        query.OrderByClause.ShouldBe("[CreatedAt] DESC, [Id] DESC");
        query.Take.ShouldBe(PageSize + 1);
        query.IsBackward.ShouldBeFalse();
        query.Parameters.ParameterNames.ShouldBeEmpty();
    }

    [Fact]
    public void Forward_descending_cursor_uses_less_than()
    {
        var query = KeysetSqlBuilder.Build(NewestFirst, Cursor(CursorDirection.Next), PageSize);

        query.WhereClause.ShouldBe("(([CreatedAt] < @k0) OR ([CreatedAt] = @k0 AND [Id] < @k1))");
        query.OrderByClause.ShouldBe("[CreatedAt] DESC, [Id] DESC");
        query.IsBackward.ShouldBeFalse();
        query.Parameters.ParameterNames.ShouldBe(["k0", "k1"], ignoreOrder: true);
        query.Parameters.Get<DateTime>("k0").ShouldBe(LastCreatedAt);
        query.Parameters.Get<Guid>("k1").ShouldBe(LastId);
    }

    [Fact]
    public void Backward_cursor_flips_comparison_and_order()
    {
        var query = KeysetSqlBuilder.Build(NewestFirst, Cursor(CursorDirection.Previous), PageSize);

        query.WhereClause.ShouldBe("(([CreatedAt] > @k0) OR ([CreatedAt] = @k0 AND [Id] > @k1))");
        query.OrderByClause.ShouldBe("[CreatedAt] ASC, [Id] ASC");
        query.IsBackward.ShouldBeTrue();
        query.Take.ShouldBe(PageSize + 1);
    }

    [Fact]
    public void Forward_ascending_cursor_with_three_terms_expands_the_row_comparison()
    {
        var sort = SortSpecification.Parse("startDate,-createdAt", Allowed, IdField, "-createdAt").Value;
        var cursor = new Cursor(CursorDirection.Next, sort.Signature, "hash", [new DateOnly(2026, 2, 2), LastCreatedAt, LastId]);

        var query = KeysetSqlBuilder.Build(sort, cursor, PageSize);

        query.WhereClause.ShouldBe(
            "(([StartDate] > @k0) OR ([StartDate] = @k0 AND [CreatedAt] < @k1) OR ([StartDate] = @k0 AND [CreatedAt] = @k1 AND [Id] < @k2))");
        query.OrderByClause.ShouldBe("[StartDate] ASC, [CreatedAt] DESC, [Id] DESC");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(CursorPageRequest.MaxPageSize + 1)]
    [InlineData(int.MaxValue)]
    public void Page_size_outside_1_to_max_throws(int pageSize)
        => Should.Throw<ArgumentOutOfRangeException>(() => KeysetSqlBuilder.Build(NewestFirst, cursor: null, pageSize));

    [Theory]
    [InlineData(1)]
    [InlineData(CursorPageRequest.MaxPageSize)]
    public void Page_size_bounds_are_accepted(int pageSize)
        => KeysetSqlBuilder.Build(NewestFirst, cursor: null, pageSize).Take.ShouldBe(pageSize + 1);

    [Fact]
    public void Cursor_with_the_wrong_number_of_key_values_throws()
        => Should.Throw<ArgumentException>(() => KeysetSqlBuilder.Build(
            NewestFirst, new Cursor(CursorDirection.Next, NewestFirst.Signature, "hash", [LastCreatedAt]), PageSize));

    private static Cursor Cursor(CursorDirection direction) => new(direction, NewestFirst.Signature, "hash", [LastCreatedAt, LastId]);
}
