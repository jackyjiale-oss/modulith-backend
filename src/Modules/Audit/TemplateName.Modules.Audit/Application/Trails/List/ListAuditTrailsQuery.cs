using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;

namespace TemplateName.Modules.Audit.Application.Trails.List;

/// <summary>
/// A page of the audit trail, optionally narrowed by every filter that is given; the filters combine with AND. <paramref name="EntityType"/>,
/// <paramref name="EntityId"/> and <paramref name="Module"/> are compared exactly, <paramref name="Action"/> is one of the
/// <c>EntityChangeAction</c> names (exact, case-sensitive), and <paramref name="From"/> and <paramref name="To"/> are the inclusive ends of
/// the time range, ISO 8601 text as the caller wrote it, which <see cref="AuditTrailTimestamp"/> reads as UTC. A blank text filter is no
/// filter.
/// </summary>
internal sealed record ListAuditTrailsQuery(
    CursorPageRequest Page,
    string? EntityType,
    string? EntityId,
    Guid? UserId,
    Guid? ChangeSetId,
    string? Module,
    string? Action,
    string? From,
    string? To) : IQuery<CursorPage<AuditTrailResponse>>;
