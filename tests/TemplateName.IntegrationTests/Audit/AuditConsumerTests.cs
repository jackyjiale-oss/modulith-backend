using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TemplateName.Application.Common.Auditing;
using TemplateName.Application.Common.Messaging;
using TemplateName.Infrastructure.Common.Inbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Audit.Application.Abstractions;
using TemplateName.Modules.Audit.Application.Trails.Record;
using TemplateName.Modules.Audit.Domain.Trails;
using TemplateName.Modules.Audit.Infrastructure.Persistence;
using TemplateName.SharedKernel.Auditing;

namespace TemplateName.IntegrationTests.Audit;

/// <summary>
/// The Audit module's consumer of <see cref="EntityChangesRecordedIntegrationEvent"/> in the real host: change sets are published through
/// the host's <see cref="IIntegrationEventPublisher"/> with an explicit id, as an owning module's outbox does, and the <c>audit</c> rows
/// are read back.
/// </summary>
public sealed class AuditConsumerTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";

    private static readonly string Consumer = typeof(EntityChangesRecordedIntegrationEventHandler).FullName!;

    [Fact]
    public async Task A_change_set_is_written_as_one_trail_row_per_change()
    {
        var changeSet = TwoChanges();

        await PublishAsync(Factory.Services, changeSet);

        var trails = await TrailsAsync();
        trails.Count.ShouldBe(2);
        trails.ShouldAllBe(trail => trail.ChangeSetId == changeSet.Id
            && trail.OccurredAt == changeSet.OccurredAt
            && trail.UserId == changeSet.UserId
            && trail.SessionId == changeSet.SessionId
            && trail.Module == "sample"
            && trail.TraceId == TraceId);
        trails.Select(trail => (trail.EntityType, trail.EntityId, trail.Action, trail.Changes))
            .ShouldBe(changeSet.Changes.Select(change => (change.EntityType, change.EntityId, change.Action, change.Changes)), ignoreOrder: true);
        (await InboxAsync(changeSet.Id)).ShouldBe([Consumer]);
    }

    [Fact]
    public async Task Values_longer_than_their_columns_are_cut_so_the_change_set_is_still_written()
    {
        var changeSet = new EntityChangesRecordedIntegrationEvent(
            Guid.NewGuid(),
            Factory.Time.GetUtcNow().AddMilliseconds(-1_250),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new string('a', AuditTrail.MaxTraceIdLength + 10),
            new string('m', AuditTrail.MaxModuleLength + 10),
            [new EntityChange(new string('t', AuditTrail.MaxEntityTypeLength + 10), new string('k', AuditTrail.MaxEntityIdLength + 10), EntityChangeAction.Created, """{"Reason":{"new":"Holiday"}}""")]);

        await Should.NotThrowAsync(() => PublishAsync(Factory.Services, changeSet));

        var trail = (await TrailsAsync()).ShouldHaveSingleItem();
        trail.Module.ShouldBe(new string('m', AuditTrail.MaxModuleLength - 1) + "…");
        trail.EntityType.ShouldBe(new string('t', AuditTrail.MaxEntityTypeLength - 1) + "…");
        trail.EntityId.ShouldBe(new string('k', AuditTrail.MaxEntityIdLength - 1) + "…");
        trail.TraceId.ShouldBe(new string('a', AuditTrail.MaxTraceIdLength - 1) + "…");
        (await InboxAsync(changeSet.Id)).ShouldBe([Consumer]);
    }

    [Fact]
    public async Task Duplicate_delivery_writes_the_rows_once()
    {
        var changeSet = TwoChanges();

        await PublishAsync(Factory.Services, changeSet);
        await PublishAsync(Factory.Services, changeSet);

        (await TrailsAsync()).Count.ShouldBe(2);
        (await InboxAsync(changeSet.Id)).ShouldBe([Consumer]);
    }

    [Fact]
    public async Task A_delivery_that_races_past_the_inbox_check_is_dropped_as_processed()
    {
        // Every check answers "not processed", as when two deliveries check before either saves: the second save then fails on the inbox key.
        await using var host = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IInbox>();
            services.AddScoped<IInbox>(serviceProvider => new NeverProcessedInbox(ActivatorUtilities.CreateInstance<ModuleInbox>(serviceProvider)));
        }));
        var changeSet = TwoChanges();

        await PublishAsync(host.Services, changeSet);
        await Should.NotThrowAsync(() => PublishAsync(host.Services, changeSet));

        (await TrailsAsync()).Select(trail => trail.ChangeSetId).ShouldBe([changeSet.Id, changeSet.Id]);
        (await InboxAsync(changeSet.Id)).ShouldBe([Consumer]);
    }

    private EntityChangesRecordedIntegrationEvent TwoChanges()
    {
        var leaveRequestId = Guid.NewGuid().ToString();
        return new EntityChangesRecordedIntegrationEvent(
            Guid.NewGuid(),
            Factory.Time.GetUtcNow().AddMilliseconds(-1_250),
            Guid.NewGuid(),
            Guid.NewGuid(),
            TraceId,
            "sample",
            [
                new EntityChange("LeaveRequest", leaveRequestId, EntityChangeAction.Created, """{"Reason":{"new":"Holiday"}}"""),
                new EntityChange("LeaveRequest", leaveRequestId, EntityChangeAction.Updated, """{"Status":{"old":"Pending","new":"Approved"}}"""),
            ]);
    }

    private static Task PublishAsync(IServiceProvider services, EntityChangesRecordedIntegrationEvent changeSet)
        => services.GetRequiredService<IIntegrationEventPublisher>().PublishAsync(changeSet, Ct);

    private async Task<List<AuditTrail>> TrailsAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Set<AuditTrail>().AsNoTracking().OrderBy(trail => trail.Id).ToListAsync(Ct);
    }

    private async Task<List<string>> InboxAsync(Guid messageId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Set<InboxMessage>()
            .Where(message => message.MessageId == messageId)
            .Select(message => message.Consumer)
            .ToListAsync(Ct);
    }

    /// <summary>The module's inbox, except that it never finds a record, so every delivery gets past the check.</summary>
    private sealed class NeverProcessedInbox(IInbox inner) : IInbox
    {
        public Task<bool> HasProcessedAsync(Guid messageId, string consumer, CancellationToken cancellationToken) => Task.FromResult(false);

        public void Record(Guid messageId, string consumer) => inner.Record(messageId, consumer);
    }
}
