using System.Text.Json;

namespace TemplateName.Modules.Audit.Application.Trails.List;

/// <summary>
/// One line of the audit trail as the list shows it: the change one save made to one audited entity. The trail holds personal data in
/// clear text (an email, a display name); only secrets are left out or masked by the owning entity when the change is recorded.
/// </summary>
/// <param name="Id">The line's number, which grows with every line; the tie-breaker of the order.</param>
/// <param name="ChangeSetId">The save the line belongs to; every line of one save shares it.</param>
/// <param name="OccurredAt">When the save happened (UTC).</param>
/// <param name="UserId">The user who made the change; null for background work.</param>
/// <param name="SessionId">The session the change was made in, if any.</param>
/// <param name="Module">The module that owns the entity (its schema, for example <c>sample</c>).</param>
/// <param name="EntityType">The type name of the entity without its namespace, for example <c>LeaveRequest</c>.</param>
/// <param name="EntityId">The key of the entity as text; a composite key is <c>Name=value;Name=value</c>.</param>
/// <param name="Action">What happened: <c>Created</c>, <c>Updated</c>, <c>Deleted</c>, <c>SoftDeleted</c> or <c>Restored</c>.</param>
/// <param name="Changes">A JSON object keyed by property name holding the old and new values (<c>{"Status":{"old":"Pending","new":"Approved"}}</c>).</param>
/// <param name="TraceId">The trace id of the request, also the <c>X-Trace-Id</c> header.</param>
internal sealed record AuditTrailResponse(
    long Id,
    Guid ChangeSetId,
    DateTime OccurredAt,
    Guid? UserId,
    Guid? SessionId,
    string Module,
    string EntityType,
    string EntityId,
    string Action,
    JsonElement Changes,
    string? TraceId);
