using FluentValidation;
using TemplateName.Application.Common.Pagination;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Application.Admin.Users.List;

internal sealed class ListUsersQueryValidator : AbstractValidator<ListUsersQuery>
{
    public ListUsersQueryValidator()
    {
        // Reported under the query-string name (errors.pageSize), not the nested property path (page.pageSize).
        RuleFor(query => query.Page.PageSize)
            .InclusiveBetween(1, CursorPageRequest.MaxPageSize)
            .OverridePropertyName(nameof(CursorPageRequest.PageSize));

        RuleFor(query => query.Search).MaximumLength(User.MaxEmailLength);

        // The query string binds a number (status=7) to the enum too; only the declared states are filters.
        RuleFor(query => query.Status).IsInEnum();
    }
}
