using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.SharedKernel.Auditing;

namespace TemplateName.UnitTests.Infrastructure.Auditing;

public sealed class OutboxEventTypesTests
{
    [Fact]
    public void Registry_built_from_an_assembly_without_domain_events_still_finds_the_change_set_event()
    {
        // System.Private.CoreLib holds no IDomainEvent; the audit interceptor writes this event to every module's outbox.
        var eventTypes = new OutboxEventTypes<AuditTestDbContext>(typeof(object).Assembly);

        eventTypes.Find(typeof(EntityChangesRecordedDomainEvent).FullName!).ShouldBe(typeof(EntityChangesRecordedDomainEvent));
    }
}
