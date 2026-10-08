using FluentValidation;
using TemplateName.Application.Common.Pagination;

namespace TemplateName.Modules.Sample.Application.LeaveRequests.List;

internal sealed class ListLeaveRequestsQueryValidator : AbstractValidator<ListLeaveRequestsQuery>
{
    public ListLeaveRequestsQueryValidator()
    {
        // Reported under the query-string name (errors.pageSize), not the nested property path (page.pageSize).
        RuleFor(query => query.Page.PageSize)
            .InclusiveBetween(1, CursorPageRequest.MaxPageSize)
            .OverridePropertyName(nameof(CursorPageRequest.PageSize));
    }
}
