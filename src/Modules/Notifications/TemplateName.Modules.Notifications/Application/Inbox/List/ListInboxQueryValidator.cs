using FluentValidation;
using TemplateName.Application.Common.Pagination;

namespace TemplateName.Modules.Notifications.Application.Inbox.List;

internal sealed class ListInboxQueryValidator : AbstractValidator<ListInboxQuery>
{
    public ListInboxQueryValidator()
    {
        // Reported under the query-string name (errors.pageSize), not the nested property path (page.pageSize).
        RuleFor(query => query.Page.PageSize)
            .InclusiveBetween(1, CursorPageRequest.MaxPageSize)
            .OverridePropertyName(nameof(CursorPageRequest.PageSize));
    }
}
