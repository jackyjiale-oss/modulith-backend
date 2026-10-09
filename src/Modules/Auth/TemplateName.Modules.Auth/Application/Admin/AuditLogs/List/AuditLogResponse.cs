namespace TemplateName.Modules.Auth.Application.Admin.AuditLogs.List;

/// <summary>
/// One audit log entry as the list shows it. The log holds no secret: a password, token or hash is never written to it, and
/// <paramref name="AttemptedIdentifier"/> was masked when it was written (<c>a****@example.com</c>).
/// </summary>
/// <param name="Id">The entry's number, which grows with every entry; the tie-breaker of the order.</param>
/// <param name="OccurredAt">When it happened (UTC).</param>
/// <param name="UserId">The user the event is about, if it is about one (for an administration action on a user, the target).</param>
/// <param name="EventType">One of the <c>AuthAuditEvents</c>, such as <c>auth.login_failed</c>.</param>
/// <param name="Succeeded">Whether the action succeeded.</param>
/// <param name="FailureReason">The error code or reason of a failure.</param>
/// <param name="AttemptedIdentifier">What the caller typed to identify themselves, masked, for events without a known user.</param>
/// <param name="IpAddress">The client address of the request.</param>
/// <param name="UserAgent">The <c>User-Agent</c> of the request.</param>
/// <param name="SessionId">The session the event belongs to, if any.</param>
/// <param name="TraceId">The trace id of the request, also the <c>X-Trace-Id</c> header.</param>
/// <param name="Details">Extra facts as JSON text, such as the acting administrator (<c>{"actorId":"…"}</c>).</param>
internal sealed record AuditLogResponse(
    long Id,
    DateTime OccurredAt,
    Guid? UserId,
    string EventType,
    bool Succeeded,
    string? FailureReason,
    string? AttemptedIdentifier,
    string? IpAddress,
    string? UserAgent,
    Guid? SessionId,
    string? TraceId,
    string? Details);
