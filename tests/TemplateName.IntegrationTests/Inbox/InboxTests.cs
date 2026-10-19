using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Infrastructure.Common.Inbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.IntegrationTests.Persistence;
using TemplateName.SharedKernel;
using TestInbox = TemplateName.Infrastructure.Common.Inbox.Inbox<TemplateName.IntegrationTests.Persistence.TestDbContext>;

namespace TemplateName.IntegrationTests.Inbox;

public sealed class InboxTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string Consumer = "TemplateName.Tests.FirstConsumer";
    private const string OtherConsumer = "TemplateName.Tests.SecondConsumer";

    private readonly List<AsyncServiceScope> _scopes = [];

    [Fact]
    public async Task Record_then_save_marks_processed()
    {
        var messageId = SequentialGuid.Create(Factory.Time.GetUtcNow());
        var (db, inbox) = NewScope();
        (await inbox.HasProcessedAsync(messageId, Consumer, Ct)).ShouldBeFalse();

        db.Add(TestAggregate.Create("a", Factory.Time.GetUtcNow()));
        inbox.Record(messageId, Consumer);

        // The consumer's own rows (here an aggregate and its outbox row) and the inbox row commit in one save.
        (await db.SaveChangesAsync(Ct)).ShouldBe(3);

        var (read, readInbox) = NewScope();
        (await readInbox.HasProcessedAsync(messageId, Consumer, Ct)).ShouldBeTrue();
        var row = await read.Set<InboxMessage>().AsNoTracking().SingleAsync(Ct);
        row.ShouldBeEquivalentTo(new InboxMessage { MessageId = messageId, Consumer = Consumer, ProcessedAt = Factory.Time.GetUtcNow().UtcDateTime });
    }

    [Fact]
    public async Task Second_record_of_the_same_message_and_consumer_is_a_duplicate()
    {
        var messageId = SequentialGuid.Create(Factory.Time.GetUtcNow());
        var (first, firstInbox) = NewScope();
        var (second, secondInbox) = NewScope();

        // Both consumers checked before either saved, so both go ahead and race to save.
        (await firstInbox.HasProcessedAsync(messageId, Consumer, Ct)).ShouldBeFalse();
        (await secondInbox.HasProcessedAsync(messageId, Consumer, Ct)).ShouldBeFalse();
        foreach (var (db, inbox) in new[] { (first, firstInbox), (second, secondInbox) })
        {
            db.Add(TestAggregate.Create("a", Factory.Time.GetUtcNow()));
            inbox.Record(messageId, Consumer);
        }

        var failures = await Task.WhenAll(TrySaveAsync(first), TrySaveAsync(second));

        failures.Count(failure => failure is null).ShouldBe(1);
        TestInbox.IsDuplicate(failures.Single(failure => failure is not null)!).ShouldBeTrue();

        // The loser's own rows rolled back with its inbox row.
        var (read, _) = NewScope();
        (await read.Set<TestAggregate>().CountAsync(Ct)).ShouldBe(1);
        (await read.Set<InboxMessage>().CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Same_message_for_another_consumer_is_not_a_duplicate()
    {
        var messageId = SequentialGuid.Create(Factory.Time.GetUtcNow());
        var (first, firstInbox) = NewScope();
        firstInbox.Record(messageId, Consumer);
        await first.SaveChangesAsync(Ct);

        var (second, secondInbox) = NewScope();
        (await secondInbox.HasProcessedAsync(messageId, OtherConsumer, Ct)).ShouldBeFalse();
        secondInbox.Record(messageId, OtherConsumer);
        await second.SaveChangesAsync(Ct);

        var (_, readInbox) = NewScope();
        (await readInbox.HasProcessedAsync(messageId, Consumer, Ct)).ShouldBeTrue();
        (await readInbox.HasProcessedAsync(messageId, OtherConsumer, Ct)).ShouldBeTrue();
        (await readInbox.HasProcessedAsync(SequentialGuid.Create(Factory.Time.GetUtcNow()), Consumer, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Consumer_of_400_characters_round_trips_and_401_is_rejected()
    {
        // MessageId (16 bytes) plus nvarchar(400) (800 bytes) keeps the clustered key under SQL Server's 900-byte limit.
        var messageId = SequentialGuid.Create(Factory.Time.GetUtcNow());
        var longest = new string('c', 400);
        var (db, inbox) = NewScope();
        db.Model.FindEntityType(typeof(InboxMessage))!.FindProperty(nameof(InboxMessage.Consumer))!.GetMaxLength().ShouldBe(400);

        inbox.Record(messageId, longest);
        await db.SaveChangesAsync(Ct);

        var (read, readInbox) = NewScope();
        (await readInbox.HasProcessedAsync(messageId, longest, Ct)).ShouldBeTrue();
        (await read.Set<InboxMessage>().AsNoTracking().SingleAsync(Ct)).Consumer.ShouldBe(longest);

        var tooLong = new string('c', 401);
        Should.Throw<ArgumentOutOfRangeException>(() => readInbox.Record(messageId, tooLong));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => readInbox.HasProcessedAsync(messageId, tooLong, Ct));
    }

    [Fact]
    public async Task Unique_violation_outside_the_inbox_is_not_a_duplicate()
    {
        var aggregate = TestAggregate.Create("a", Factory.Time.GetUtcNow());
        var (first, _) = NewScope();
        first.Add(aggregate);
        await first.SaveChangesAsync(Ct);

        // The same aggregate inserted again fails on its own primary key, which a consumer must not mistake for "already processed".
        var (second, _) = NewScope();
        second.Add(aggregate);
        var exception = await Should.ThrowAsync<DbUpdateException>(() => second.SaveChangesAsync(Ct));

        TestInbox.IsDuplicate(exception).ShouldBeFalse();
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes)
        {
            await scope.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    private static async Task<DbUpdateException?> TrySaveAsync(TestDbContext db)
    {
        try
        {
            await db.SaveChangesAsync(Ct);
            return null;
        }
        catch (DbUpdateException exception)
        {
            return exception;
        }
    }

    private (TestDbContext Db, TestInbox Inbox) NewScope()
    {
        var scope = Factory.Services.CreateAsyncScope();
        _scopes.Add(scope);
        return (scope.ServiceProvider.GetRequiredService<TestDbContext>(), scope.ServiceProvider.GetRequiredService<TestInbox>());
    }
}
