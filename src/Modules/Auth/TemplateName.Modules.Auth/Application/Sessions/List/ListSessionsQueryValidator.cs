using FluentValidation;
using TemplateName.Application.Common.Pagination;

namespace TemplateName.Modules.Auth.Application.Sessions.List;

internal sealed class ListSessionsQueryValidator : AbstractValidator<ListSessionsQuery>
{
    public ListSessionsQueryValidator()
    {
        // Reported under the query-string name (errors.pageSize), not the nested property path (page.pageSize).
        RuleFor(query => query.Page.PageSize)
            .InclusiveBetween(1, CursorPageRequest.MaxPageSize)
            .OverridePropertyName(nameof(CursorPageRequest.PageSize));
    }
}
