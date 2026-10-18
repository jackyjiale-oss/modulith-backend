using FluentValidation;
using TemplateName.Application.Common.Pagination;

namespace TemplateName.Modules.Auth.Application.TrustedDevices.List;

internal sealed class ListTrustedDevicesQueryValidator : AbstractValidator<ListTrustedDevicesQuery>
{
    public ListTrustedDevicesQueryValidator()
    {
        // Reported under the query-string name (errors.pageSize), not the nested property path (page.pageSize).
        RuleFor(query => query.Page.PageSize)
            .InclusiveBetween(1, CursorPageRequest.MaxPageSize)
            .OverridePropertyName(nameof(CursorPageRequest.PageSize));
    }
}
