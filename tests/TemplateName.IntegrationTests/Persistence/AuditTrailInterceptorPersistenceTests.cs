using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Auditing;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.SharedKernel.Auditing;

namespace TemplateName.IntegrationTests.Persistence;

public sealed class AuditTrailInterceptorPersistenceTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private static readonly Guid UserA = Guid.Parse("5d0a3c1e-7b2f-4e8a-9c6d-1f2e3a4b5c6d");
    private static readonly string AuditMessageType = typeof(EntityChangesRecordedDomainEvent).FullName!;

    private readonly List<AsyncServiceScope> _scopes = [];

    [Fact]
    public async Task Audited_change_writes_one_audit_outbox_row_in_the_same_save()
    {
        Factory.CurrentUser.UserId = UserA;
        var aggregate = AuditedTestAggregate.Create("First", Factory.Time.GetUtcNow());

        await using (var db = NewTestDbContext())
        {
            db.Add(aggregate);

            // One save writes the entity and its audit row.
            (await db.SaveChangesAsync(Ct)).ShouldBe(2);
        }

        var message = await SingleAuditMessageAsync();
        message.OccurredAt.ShouldBe(Factory.Time.GetUtcNow().UtcDateTime);
        var changeSet = Read(message);
        changeSet.UserId.ShouldBe(UserA);
        changeSet.Module.ShouldBe(TestDbContext.Schema);
        var change = changeSet.Changes.ShouldHaveSingleItem();
        change.EntityType.ShouldBe(nameof(AuditedTestAggregate));
        change.EntityId.ShouldBe(aggregate.Id.ToString());
        change.Action.ShouldBe(EntityChangeAction.Created);

        // The stamps, the row version and the ignored counter are left out.
        JsonNode.Parse(change.Changes)!.AsObject().Select(property => property.Key).ShouldBe(["Id", "Title"], ignoreOrder: true);
    }

    [Fact]
    public async Task Save_that_changes_only_an_ignored_property_writes_no_audit_row()
    {
        var aggregate = await SaveNewAggregateWithoutAuditRowAsync();

        await using (var db = NewTestDbContext())
        {
            var loaded = await db.Set<AuditedTestAggregate>().SingleAsync(entity => entity.Id == aggregate.Id, Ct);
            loaded.Counter++;

            (await db.SaveChangesAsync(Ct)).ShouldBe(1);
        }

        (await CountAuditMessagesAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Save_with_no_signed_in_user_records_null_user_and_session()
    {
        await using (var db = NewTestDbContext())
        {
            db.Add(AuditedTestAggregate.Create("First", Factory.Time.GetUtcNow()));
            await db.SaveChangesAsync(Ct);
        }

        var changeSet = Read(await SingleAuditMessageAsync());
        changeSet.UserId.ShouldBeNull();
        changeSet.SessionId.ShouldBeNull();
    }

    [Fact]
    public async Task Failed_save_retried_on_the_same_context_records_the_change_once()
    {
        var aggregate = await SaveNewAggregateWithoutAuditRowAsync();

        await using var db = NewTestDbContext();
        var loaded = await db.Set<AuditedTestAggregate>().SingleAsync(entity => entity.Id == aggregate.Id, Ct);
        loaded.Title = "Second";
        await ChangeRowVersionElsewhereAsync(aggregate.Id);

        var conflict = await Should.ThrowAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync(Ct));
        await ResolveAsync(conflict);
        await db.SaveChangesAsync(Ct);

        var change = Read(await SingleAuditMessageAsync()).Changes.ShouldHaveSingleItem();
        change.Action.ShouldBe(EntityChangeAction.Updated);
        var expected = JsonNode.Parse("""{ "Title": { "old": "First", "new": "Second" } }""");
        JsonNode.DeepEquals(JsonNode.Parse(change.Changes), expected).ShouldBeTrue(change.Changes);
    }

    [Fact]
    public async Task Failed_save_retried_with_nothing_audited_left_writes_no_audit_row()
    {
        var aggregate = await SaveNewAggregateWithoutAuditRowAsync();

        await using var db = NewTestDbContext();
        var loaded = await db.Set<AuditedTestAggregate>().SingleAsync(entity => entity.Id == aggregate.Id, Ct);
        loaded.Title = "Second";
        await ChangeRowVersionElsewhereAsync(aggregate.Id);

        var conflict = await Should.ThrowAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync(Ct));
        await ResolveAsync(conflict);
        loaded.Title = "First";
        await db.SaveChangesAsync(Ct);

        (await CountAuditMessagesAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Dispatching_the_row_publishes_the_integration_event_with_the_outbox_message_id()
    {
        Factory.CurrentUser.UserId = UserA;
        var aggregate = AuditedTestAggregate.Create("First", Factory.Time.GetUtcNow());
        await using (var db = NewTestDbContext())
        {
            db.Add(aggregate);
            await db.SaveChangesAsync(Ct);
        }

        var message = await SingleAuditMessageAsync();

        (await Factory.Services.GetRequiredService<OutboxDispatcher<TestDbContext>>().ProcessBatchAsync(Ct)).ShouldBe(1);

        var published = Factory.IntegrationEvents.Events.ShouldHaveSingleItem().ShouldBeOfType<EntityChangesRecordedIntegrationEvent>();
        published.Id.ShouldBe(message.Id);
        published.OccurredAt.ShouldBe(new DateTimeOffset(message.OccurredAt));
        published.UserId.ShouldBe(UserA);
        published.Module.ShouldBe(TestDbContext.Schema);
        var change = published.Changes.ShouldHaveSingleItem();
        change.EntityId.ShouldBe(aggregate.Id.ToString());
        change.Action.ShouldBe(EntityChangeAction.Created);
        (await SingleAuditMessageAsync()).ProcessedAt.ShouldNotBeNull();
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes)
        {
            await scope.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    private static EntityChangesRecordedDomainEvent Read(OutboxMessage message)
        => JsonSerializer.Deserialize<EntityChangesRecordedDomainEvent>(message.Content)!;

    /// <summary>Takes the client-wins side of a conflict: the database values become the original values, the edits stay.</summary>
    private static async Task ResolveAsync(DbUpdateConcurrencyException conflict)
    {
        foreach (var entry in conflict.Entries)
        {
            entry.OriginalValues.SetValues((await entry.GetDatabaseValuesAsync(Ct))!);
        }
    }

    private TestDbContext NewTestDbContext()
    {
        var scope = Factory.Services.CreateAsyncScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<TestDbContext>();
    }

    /// <summary>Saves a new aggregate titled <c>First</c>, then deletes its audit row, so a test sees only the rows of its own saves.</summary>
    private async Task<AuditedTestAggregate> SaveNewAggregateWithoutAuditRowAsync()
    {
        var aggregate = AuditedTestAggregate.Create("First", Factory.Time.GetUtcNow());
        await using var db = NewTestDbContext();
        db.Add(aggregate);
        await db.SaveChangesAsync(Ct);
        await db.Set<OutboxMessage>().ExecuteDeleteAsync(Ct);
        return aggregate;
    }

    /// <summary>Changes the row outside any interceptor (an ignored column), so SQL Server gives it a new row version.</summary>
    private async Task ChangeRowVersionElsewhereAsync(Guid id)
    {
        await using var other = NewTestDbContext();
        await other.Set<AuditedTestAggregate>()
            .Where(entity => entity.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.Counter, 7), Ct);
    }

    private async Task<OutboxMessage> SingleAuditMessageAsync()
    {
        await using var db = NewTestDbContext();
        return await db.Set<OutboxMessage>().AsNoTracking().SingleAsync(message => message.Type == AuditMessageType, Ct);
    }

    private async Task<int> CountAuditMessagesAsync()
    {
        await using var db = NewTestDbContext();
        return await db.Set<OutboxMessage>().CountAsync(message => message.Type == AuditMessageType, Ct);
    }
}
