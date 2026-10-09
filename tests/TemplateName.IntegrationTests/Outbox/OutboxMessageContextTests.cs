using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Messaging;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.IntegrationTests.Persistence;

namespace TemplateName.IntegrationTests.Outbox;

public sealed class OutboxMessageContextTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task Handler_sees_the_outbox_message_id_and_the_same_id_on_retry()
    {
        // Only a noted event: its one handler records the message context and fails the first time.
        var aggregate = TestAggregate.Create("a", Factory.Time.GetUtcNow());
        aggregate.ClearDomainEvents();
        aggregate.Note();
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            db.Add(aggregate);
            await db.SaveChangesAsync(Ct);
        }

        var message = await SingleMessageAsync();
        Factory.FlakySwitch.FailNext = true;
        var dispatcher = Factory.Services.GetRequiredService<OutboxDispatcher<TestDbContext>>();

        (await dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);
        (await SingleMessageAsync()).AttemptCount.ShouldBe(1);
        Factory.Time.Advance(TimeSpan.FromSeconds(5));
        (await dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);

        (await SingleMessageAsync()).ProcessedAt.ShouldNotBeNull();
        var expected = (message.Id, new DateTimeOffset(message.OccurredAt));
        Factory.MessageContextRecorder.Messages.ShouldBe([expected, expected]);
    }

    [Fact]
    public async Task Reading_the_context_outside_a_dispatch_throws()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var messageContext = scope.ServiceProvider.GetRequiredService<IOutboxMessageContext>();

        Should.Throw<InvalidOperationException>(() => messageContext.MessageId).Message.ShouldContain("outbox dispatch");
        Should.Throw<InvalidOperationException>(() => messageContext.OccurredAt).Message.ShouldContain("outbox dispatch");
    }

    private async Task<OutboxMessage> SingleMessageAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TestDbContext>().Set<OutboxMessage>().AsNoTracking().SingleAsync(Ct);
    }
}
