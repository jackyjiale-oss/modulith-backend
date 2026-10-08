using FluentValidation;
using TemplateName.Application.Common.Pagination;
using TemplateName.Modules.Auth.Domain.Audit;

namespace TemplateName.Modules.Auth.Application.Admin.AuditLogs.List;

internal sealed class ListAuditLogsQueryValidator : AbstractValidator<ListAuditLogsQuery>
{
    private const string TimestampMessage = "'{PropertyName}' must be an ISO 8601 date or date-time, such as 2026-10-09 or 2026-10-09T08:30:00Z.";

    public ListAuditLogsQueryValidator()
    {
        // Reported under the query-string name (errors.pageSize), not the nested property path (page.pageSize).
        RuleFor(query => query.Page.PageSize)
            .InclusiveBetween(1, CursorPageRequest.MaxPageSize)
            .OverridePropertyName(nameof(CursorPageRequest.PageSize));

        // The event types are the constants of AuthAuditEvents, compared exactly; a blank value is no filter.
        RuleFor(query => query.EventType)
            .Must(eventType => AuthAuditEvents.All.Contains(eventType!))
            .When(query => !string.IsNullOrWhiteSpace(query.EventType))
            .WithMessage("'{PropertyName}' must be one of the audit event types, such as auth.login_failed.");

        RuleFor(query => query.From)
            .Must(from => AuditLogTimestamp.TryParse(from, out _))
            .When(query => !string.IsNullOrWhiteSpace(query.From))
            .WithMessage(TimestampMessage);

        RuleFor(query => query.To)
            .Must(to => AuditLogTimestamp.TryParse(to, out _))
            .When(query => !string.IsNullOrWhiteSpace(query.To))
            .WithMessage(TimestampMessage);

        // Compared as UTC instants, so the same instant written with two offsets is an (inclusive) range of one moment.
        RuleFor(query => query.To)
            .Must((query, to) => AuditLogTimestamp.TryParse(query.From, out var from) && AuditLogTimestamp.TryParse(to, out var end) && end >= from)
            .When(query => AuditLogTimestamp.TryParse(query.From, out _) && AuditLogTimestamp.TryParse(query.To, out _))
            .WithMessage("'{PropertyName}' must not be earlier than 'From'.");
    }
}
