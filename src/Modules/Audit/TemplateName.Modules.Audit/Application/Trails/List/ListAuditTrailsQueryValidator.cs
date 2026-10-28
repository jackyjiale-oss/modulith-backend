using FluentValidation;
using TemplateName.Application.Common.Pagination;
using TemplateName.SharedKernel.Auditing;

namespace TemplateName.Modules.Audit.Application.Trails.List;

internal sealed class ListAuditTrailsQueryValidator : AbstractValidator<ListAuditTrailsQuery>
{
    private const string TimestampMessage = "'{PropertyName}' must be an ISO 8601 date or date-time, such as 2026-10-09 or 2026-10-09T08:30:00Z.";

    // Compared exactly: the stored names are the enum names, so "created" is not an action.
    private static readonly string[] ActionNames = Enum.GetNames<EntityChangeAction>();

    public ListAuditTrailsQueryValidator()
    {
        // Reported under the query-string name (errors.pageSize), not the nested property path (page.pageSize).
        RuleFor(query => query.Page.PageSize)
            .InclusiveBetween(1, CursorPageRequest.MaxPageSize)
            .OverridePropertyName(nameof(CursorPageRequest.PageSize));

        // A blank value is no filter.
        RuleFor(query => query.Action)
            .Must(action => ActionNames.Contains(action!, StringComparer.Ordinal))
            .When(query => !string.IsNullOrWhiteSpace(query.Action))
            .WithMessage("'{PropertyName}' must be one of the entity change actions: Created, Updated, Deleted, SoftDeleted or Restored.");

        RuleFor(query => query.From)
            .Must(from => AuditTrailTimestamp.TryParse(from, out _))
            .When(query => !string.IsNullOrWhiteSpace(query.From))
            .WithMessage(TimestampMessage);

        RuleFor(query => query.To)
            .Must(to => AuditTrailTimestamp.TryParse(to, out _))
            .When(query => !string.IsNullOrWhiteSpace(query.To))
            .WithMessage(TimestampMessage);

        // Compared as UTC instants, so the same instant written with two offsets is an (inclusive) range of one moment.
        RuleFor(query => query.To)
            .Must((query, to) => AuditTrailTimestamp.TryParse(query.From, out var from) && AuditTrailTimestamp.TryParse(to, out var end) && end >= from)
            .When(query => AuditTrailTimestamp.TryParse(query.From, out _) && AuditTrailTimestamp.TryParse(query.To, out _))
            .WithMessage("'{PropertyName}' must not be earlier than 'From'.");
    }
}
