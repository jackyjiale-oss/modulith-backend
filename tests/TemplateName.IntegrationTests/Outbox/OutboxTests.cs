using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.IntegrationTests.Persistence;

namespace TemplateName.IntegrationTests.Outbox;

public sealed class OutboxTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private readonly List<AsyncServiceScope> _scopes = [];

    private DateTime Now => Factory.Time.GetUtcNow().UtcDateTime;

    private OutboxDispatcher<TestDbContext> Dispatcher => Factory.Services.GetRequiredService<OutboxDispatcher<TestDbContext>>();

    [Fact]
    public async Task Saving_aggregate_writes_event_to_outbox_in_same_save()
    {
        var aggregate = TestAggregate.Create("a", Factory.Time.GetUtcNow());

        await using (var db = NewTestDbContext())
        {
            db.Add(aggregate);

            // One save writes the aggregate and its outbox row.
            (await db.SaveChangesAsync(Ct)).ShouldBe(2);
        }

        aggregate.DomainEvents.ShouldBeEmpty();
        var message = await SingleMessageAsync();
        message.Type.ShouldBe(typeof(TestAggregateCreatedDomainEvent).FullName);
        message.OccurredAt.ShouldBe(Now);
        message.ProcessedAt.ShouldBeNull();
        JsonSerializer.Deserialize<TestAggregateCreatedDomainEvent>(message.Content).ShouldBe(new TestAggregateCreatedDomainEvent(aggregate.Id));
    }

    [Fact]
    public async Task Event_raised_by_an_unchanged_tracked_aggregate_is_written_to_the_outbox()
    {
        var saved = await SaveNewAggregateAsync("a");
        await using (var cleanup = NewTestDbContext())
        {
            await cleanup.Set<OutboxMessage>().ExecuteDeleteAsync(Ct);
        }

        await using (var db = NewTestDbContext())
        {
            // Loaded and tracked, then an event without any state change: the entry stays Unchanged (as on a duplicate registration),
            // and nothing else is pending in the save.
            var loaded = await db.Set<TestAggregate>().SingleAsync(aggregate => aggregate.Id == saved.Id, Ct);
            loaded.Note();
            db.Entry(loaded).State.ShouldBe(EntityState.Unchanged);

            (await db.SaveChangesAsync(Ct)).ShouldBe(1);
            loaded.DomainEvents.ShouldBeEmpty();
        }

        var message = await SingleMessageAsync();
        message.Type.ShouldBe(typeof(TestAggregateNotedDomainEvent).FullName);
        JsonSerializer.Deserialize<TestAggregateNotedDomainEvent>(message.Content).ShouldBe(new TestAggregateNotedDomainEvent(saved.Id));
    }

    [Fact]
    public async Task ProcessBatch_dispatches_and_marks_processed()
    {
        var aggregate = await SaveNewAggregateAsync("a");

        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);

        Factory.EventRecorder.Events.ShouldHaveSingleItem().ShouldBe(new TestAggregateCreatedDomainEvent(aggregate.Id));
        var message = await SingleMessageAsync();
        message.ProcessedAt.ShouldBe(Now);
        message.LockedUntil.ShouldBeNull();
        message.AttemptCount.ShouldBe(0);
    }

    [Fact]
    public async Task Failing_handler_is_retried_and_successful_handler_is_not_rerun()
    {
        await SaveNewAggregateAsync("a");
        Factory.FlakySwitch.FailNext = true;

        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);

        var failed = await SingleMessageAsync();
        failed.AttemptCount.ShouldBe(1);
        failed.Error.ShouldNotBeNull();
        failed.NextAttemptAt.ShouldBe(Now.AddSeconds(5));
        failed.LockedUntil.ShouldBeNull();
        failed.ProcessedAt.ShouldBeNull();
        Factory.EventRecorder.Events.Count.ShouldBe(1);

        // Not due yet.
        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(0);

        Factory.Time.Advance(TimeSpan.FromSeconds(5));
        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);

        var processed = await SingleMessageAsync();
        processed.ProcessedAt.ShouldBe(Now);
        processed.AttemptCount.ShouldBe(1);

        // The recording handler succeeded on the first attempt, so the retry skipped it.
        Factory.EventRecorder.Events.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Message_is_abandoned_after_max_attempts()
    {
        await SaveNewAggregateAsync("a");
        Factory.FlakySwitch.FailAlways = true;

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);
            Factory.Time.Advance(TimeSpan.FromHours(1));
        }

        var abandoned = await SingleMessageAsync();
        abandoned.AttemptCount.ShouldBe(5);
        abandoned.NextAttemptAt.ShouldBeNull();
        abandoned.ProcessedAt.ShouldBeNull();
        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_dispatchers_process_each_message_once()
    {
        var ids = new List<Guid>();
        await using (var db = NewTestDbContext())
        {
            for (var i = 0; i < 50; i++)
            {
                var aggregate = TestAggregate.Create($"a{i}", Factory.Time.GetUtcNow());
                ids.Add(aggregate.Id);
                db.Add(aggregate);
            }

            await db.SaveChangesAsync(Ct);
        }

        var first = ActivatorUtilities.CreateInstance<OutboxDispatcher<TestDbContext>>(Factory.Services);
        var second = ActivatorUtilities.CreateInstance<OutboxDispatcher<TestDbContext>>(Factory.Services);

        var claimed = await Task.WhenAll(Task.Run(() => DrainAsync(first), Ct), Task.Run(() => DrainAsync(second), Ct));

        claimed.Sum().ShouldBe(50);
        var received = Factory.EventRecorder.Events.Cast<TestAggregateCreatedDomainEvent>().Select(domainEvent => domainEvent.Id).ToList();
        received.Count.ShouldBe(50);
        received.Distinct().Count().ShouldBe(50);
        received.ShouldBe(ids, ignoreOrder: true);

        await using var read = NewTestDbContext();
        (await read.Set<OutboxMessage>().CountAsync(message => message.ProcessedAt == null, Ct)).ShouldBe(0);

        // Three handlers per message, each recorded once.
        (await read.Set<OutboxMessageConsumer>().CountAsync(Ct)).ShouldBe(150);
    }

    [Fact]
    public async Task Dispatcher_that_lost_its_lease_does_not_overwrite_the_new_owner()
    {
        var first = await SaveNewAggregateAsync("a");
        var second = await SaveNewAggregateAsync("b");
        var leaseDuration = Factory.Services.GetRequiredService<IOptions<OutboxOptions>>().Value.LeaseDuration;
        var loser = ActivatorUtilities.CreateInstance<OutboxDispatcher<TestDbContext>>(Factory.Services);
        var winner = ActivatorUtilities.CreateInstance<OutboxDispatcher<TestDbContext>>(Factory.Services);

        // The loser claims both messages and is parked inside the first one.
        var entered = Factory.HandlerGate.Close();
        var losing = Task.Run(() => loser.ProcessBatchAsync(Ct), Ct);
        var gatedId = await entered.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // Its lease runs out, so the winner re-claims both messages and completes them.
        Factory.Time.Advance(leaseDuration + TimeSpan.FromSeconds(1));
        var winnerNow = Now;
        (await winner.ProcessBatchAsync(Ct)).ShouldBe(2);

        // The loser resumes later; nothing it does may change the winner's outcome.
        Factory.Time.Advance(TimeSpan.FromSeconds(1));
        Factory.HandlerGate.Open();
        (await losing.WaitAsync(TimeSpan.FromSeconds(30), Ct)).ShouldBe(2);

        await using var db = NewTestDbContext();
        var messages = await db.Set<OutboxMessage>().AsNoTracking().ToListAsync(Ct);
        messages.Count.ShouldBe(2);
        messages.ShouldAllBe(message => message.ProcessedAt == winnerNow
            && message.AttemptCount == 0
            && message.Error == null
            && message.NextAttemptAt == null
            && message.LockedUntil == null);
        (await db.Set<OutboxMessageConsumer>().CountAsync(Ct)).ShouldBe(6);

        // The loser saw its lease had expired and did not start the second message.
        var notGatedId = first.Id == gatedId ? second.Id : first.Id;
        Factory.EventRecorder.Events.Cast<TestAggregateCreatedDomainEvent>().Count(domainEvent => domainEvent.Id == notGatedId).ShouldBe(1);
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes)
        {
            await scope.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    private static async Task<int> DrainAsync(OutboxDispatcher<TestDbContext> dispatcher)
    {
        var total = 0;
        int claimed;
        while ((claimed = await dispatcher.ProcessBatchAsync(Ct)) > 0)
        {
            total += claimed;
        }

        return total;
    }

    private TestDbContext NewTestDbContext()
    {
        var scope = Factory.Services.CreateAsyncScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<TestDbContext>();
    }

    private async Task<TestAggregate> SaveNewAggregateAsync(string name)
    {
        var aggregate = TestAggregate.Create(name, Factory.Time.GetUtcNow());
        await using var db = NewTestDbContext();
        db.Add(aggregate);
        await db.SaveChangesAsync(Ct);
        return aggregate;
    }

    private async Task<OutboxMessage> SingleMessageAsync()
    {
        await using var db = NewTestDbContext();
        return await db.Set<OutboxMessage>().AsNoTracking().SingleAsync(Ct);
    }
}
