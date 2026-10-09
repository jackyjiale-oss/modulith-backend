using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;

namespace TemplateName.Modules.Auth.Application.Admin.AuditLogs.List;

/// <summary>
/// A page of the audit log, optionally only the entries about <paramref name="UserId"/>, of one <paramref name="EventType"/> (one of
/// <c>AuthAuditEvents</c>), with the given outcome, and from <paramref name="From"/> to <paramref name="To"/> inclusive. The two times
/// are ISO 8601 text as the caller wrote them; <see cref="AuditLogTimestamp"/> reads them as UTC. A blank <paramref name="EventType"/>,
/// <paramref name="From"/> or <paramref name="To"/> is no filter.
/// </summary>
internal sealed record ListAuditLogsQuery(Guid? UserId, string? EventType, bool? Succeeded, string? From, string? To, CursorPageRequest Page)
    : IQuery<CursorPage<AuditLogResponse>>;
