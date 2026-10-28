using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.SharedKernel.Auditing;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// Leaves the audit rows (<see cref="EntityChangesRecordedDomainEvent"/>) out of an outbox query. Every save that changes an audited
/// entity adds one, so a test that counts a module's outbox rows, takes the single one or lists them by type for its own events applies
/// this; a test that scans outbox text for secrets does not, so it proves no secret reaches an audit row either.
/// </summary>
internal static class AuditOutboxFilter
{
    private static readonly string AuditRowType = typeof(EntityChangesRecordedDomainEvent).FullName!;

    public static IQueryable<OutboxMessage> WithoutAuditRows(this IQueryable<OutboxMessage> messages)
        => messages.Where(message => message.Type != AuditRowType);
}
