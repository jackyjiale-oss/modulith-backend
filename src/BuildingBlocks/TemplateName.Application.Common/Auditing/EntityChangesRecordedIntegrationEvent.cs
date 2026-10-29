using TemplateName.SharedKernel;
using TemplateName.SharedKernel.Auditing;

namespace TemplateName.Application.Common.Auditing;

/// <summary>
/// The audited entity changes of one save, for the Audit module. It lives here and not in a module's contracts because every module's
/// outbox publishes it.
/// </summary>
/// <param name="Id">The outbox message id of the domain event behind it, which is also the id of the change set; the same on every republish.</param>
/// <param name="OccurredAt">When the save happened (UTC).</param>
/// <param name="UserId">The user who made the change, or <see langword="null"/> when there was none.</param>
/// <param name="SessionId">The session the change was made in, or <see langword="null"/> when there was none.</param>
/// <param name="TraceId">The W3C trace id of the request or job that made the change, or <see langword="null"/> when there was none.</param>
/// <param name="Module">The name of the module whose context was saved.</param>
/// <param name="Changes">One entry per audited entity changed by the save.</param>
public sealed record EntityChangesRecordedIntegrationEvent(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? UserId,
    Guid? SessionId,
    string? TraceId,
    string Module,
    IReadOnlyList<EntityChange> Changes) : IIntegrationEvent;
