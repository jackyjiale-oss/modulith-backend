using TemplateName.Application.Common.Pagination;

namespace TemplateName.UnitTests.Pagination;

public sealed class SortSpecificationTests
{
    private static readonly SortField CreatedAt = new("createdAt", "[CreatedAt]", typeof(DateTime));
    private static readonly SortField StartDate = new("startDate", "[StartDate]", typeof(DateOnly));
    private static readonly SortField IdField = new("id", "[Id]", typeof(Guid));
    private static readonly SortField[] Allowed = [CreatedAt, StartDate];

    [Fact]
    public void Appends_id_tiebreaker_with_last_direction()
        => SortSpecification.Parse("-createdAt", Allowed, IdField, "-createdAt").Value.Signature.ShouldBe("-createdAt,-id");

    [Fact]
    public void Empty_sort_uses_default()
    {
        SortSpecification.Parse(null, Allowed, IdField, "-createdAt").Value.Signature.ShouldBe("-createdAt,-id");
        SortSpecification.Parse("  ", Allowed, IdField, "-createdAt").Value.Signature.ShouldBe("-createdAt,-id");
    }

    [Fact]
    public void Several_terms_keep_their_order_and_directions()
    {
        var sort = SortSpecification.Parse("startDate, -createdAt", Allowed, IdField, "-createdAt").Value;

        sort.Signature.ShouldBe("startDate,-createdAt,-id");
        sort.Terms.ShouldBe([(StartDate, false), (CreatedAt, true), (IdField, true)]);
    }

    [Fact]
    public void Unknown_field_is_rejected()
    {
        var result = SortSpecification.Parse("reason", Allowed, IdField, "-createdAt");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("pagination.invalid_sort");
        result.Error.Parameters!["allowed"].ShouldBe("createdAt,startDate");
    }

    [Theory]
    [InlineData("createdAt,-createdAt")]
    [InlineData("startDate,startDate")]
    public void Duplicate_field_is_rejected(string sort)
        => SortSpecification.Parse(sort, Allowed, IdField, "-createdAt").Error.Code.ShouldBe("pagination.invalid_sort");

    [Theory]
    [InlineData("id")]
    [InlineData("-")]
    [InlineData("createdAt,")]
    [InlineData("CreatedAt")]
    [InlineData("--createdAt")]
    public void Id_empty_terms_and_wrong_casing_are_rejected(string sort)
        => SortSpecification.Parse(sort, Allowed, IdField, "-createdAt").Error.Code.ShouldBe("pagination.invalid_sort");
}
