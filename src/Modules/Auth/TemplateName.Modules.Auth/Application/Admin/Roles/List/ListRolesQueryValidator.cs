using FluentValidation;
using TemplateName.Application.Common.Pagination;

namespace TemplateName.Modules.Auth.Application.Admin.Roles.List;

internal sealed class ListRolesQueryValidator : AbstractValidator<ListRolesQuery>
{
    public ListRolesQueryValidator()
    {
        // Reported under the query-string name (errors.pageSize), not the nested property path (page.pageSize).
        RuleFor(query => query.Page.PageSize)
            .InclusiveBetween(1, CursorPageRequest.MaxPageSize)
            .OverridePropertyName(nameof(CursorPageRequest.PageSize));
    }
}
