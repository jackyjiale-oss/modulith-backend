using FluentValidation.TestHelper;
using TemplateName.Application.Common.Pagination;
using TemplateName.Modules.Sample.Application.LeaveRequests.List;

namespace TemplateName.UnitTests.Sample;

public sealed class ListLeaveRequestsQueryValidatorTests
{
    private readonly ListLeaveRequestsQueryValidator _sut = new();

    [Theory]
    [InlineData(1)]
    [InlineData(CursorPageRequest.DefaultPageSize)]
    [InlineData(CursorPageRequest.MaxPageSize)]
    public void Page_size_from_1_to_100_passes(int pageSize)
        => _sut.TestValidate(Query(pageSize)).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(CursorPageRequest.MaxPageSize + 1)]
    public void Page_size_outside_1_to_100_fails_under_the_query_parameter_name(int pageSize)
        => _sut.TestValidate(Query(pageSize)).ShouldHaveValidationErrorFor(nameof(CursorPageRequest.PageSize));

    private static ListLeaveRequestsQuery Query(int pageSize) => new(null, null, new CursorPageRequest(pageSize));
}
