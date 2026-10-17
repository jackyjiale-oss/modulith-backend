using FluentValidation;
using TemplateName.Application.Common.Pagination;
using TemplateName.Modules.Notifications.Application.Catalog;

namespace TemplateName.Modules.Notifications.Application.Admin.Deliveries.List;

internal sealed class ListDeliveriesQueryValidator : AbstractValidator<ListDeliveriesQuery>
{
    public ListDeliveriesQueryValidator()
    {
        // Reported under the query-string name (errors.pageSize), not the nested property path (page.pageSize).
        RuleFor(query => query.Page.PageSize)
            .InclusiveBetween(1, CursorPageRequest.MaxPageSize)
            .OverridePropertyName(nameof(CursorPageRequest.PageSize));

        // The query string binds a number (status=7) to an enum too; only the declared values are filters.
        RuleFor(query => query.Status).IsInEnum();
        RuleFor(query => query.Channel).IsInEnum();

        // Only the length is checked, not the catalog: the list is read-only, and an administrator looking for a type that was removed
        // from the code still finds its old deliveries.
        RuleFor(query => query.TypeCode).MaximumLength(NotificationCatalog.MaxCodeLength);
    }
}
