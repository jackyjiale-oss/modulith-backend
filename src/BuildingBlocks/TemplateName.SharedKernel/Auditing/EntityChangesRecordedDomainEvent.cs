namespace TemplateName.SharedKernel.Auditing;

/// <summary>
/// The audited entity changes of one save. It is the payload of an outbox row written by the audit interceptor and is not raised by
/// an aggregate. Its name is stored in outbox rows, so renaming it would orphan pending messages.
/// </summary>
/// <param name="UserId">The user who made the change, or <see langword="null"/> when there was none (a job, a seed).</param>
/// <param name="SessionId">The session the change was made in, or <see langword="null"/> when there was none.</param>
/// <param name="TraceId">The W3C trace id of the request or job that made the change, or <see langword="null"/> when there was none.</param>
/// <param name="Module">The name of the module whose context was saved.</param>
/// <param name="Changes">One entry per audited entity changed by the save.</param>
public sealed record EntityChangesRecordedDomainEvent(
    Guid? UserId,
    Guid? SessionId,
    string? TraceId,
    string Module,
    IReadOnlyList<EntityChange> Changes) : IDomainEvent;
