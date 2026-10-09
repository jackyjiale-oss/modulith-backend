using TemplateName.Modules.Notifications.Domain.HubTickets;

namespace TemplateName.UnitTests.Notifications;

public sealed class HubTicketTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private static byte[] Hash(byte seed) => [.. Enumerable.Repeat(seed, 32)];

    [Fact]
    public void Issue_stores_the_hash_and_expires_after_the_lifetime()
    {
        var userId = Guid.NewGuid();

        var ticket = HubTicket.Issue(userId, Hash(1), Lifetime, Now);

        ticket.TokenHash.ShouldBe(Hash(1));
        ticket.UserId.ShouldBe(userId);
        ticket.CreatedAt.ShouldBe(Now);
        ticket.ExpiresAt.ShouldBe(Now + Lifetime);
        ticket.ConsumedAt.ShouldBeNull();
    }

    [Fact]
    public void HubTicket_cannot_be_consumed_at_or_after_expiry_or_twice()
    {
        var ticket = HubTicket.Issue(Guid.NewGuid(), Hash(1), Lifetime, Now);

        ticket.CanConsume(Now).ShouldBeTrue();
        ticket.CanConsume(Now + Lifetime - TimeSpan.FromTicks(1)).ShouldBeTrue();
        ticket.CanConsume(Now + Lifetime).ShouldBeFalse();
        ticket.CanConsume(Now + Lifetime + TimeSpan.FromSeconds(1)).ShouldBeFalse();

        ticket.TryConsume(Now + Lifetime).ShouldBeFalse();
        ticket.ConsumedAt.ShouldBeNull();

        ticket.TryConsume(Now.AddSeconds(5)).ShouldBeTrue();
        ticket.ConsumedAt.ShouldBe(Now.AddSeconds(5));
        ticket.CanConsume(Now.AddSeconds(6)).ShouldBeFalse();
        ticket.TryConsume(Now.AddSeconds(6)).ShouldBeFalse();
        ticket.ConsumedAt.ShouldBe(Now.AddSeconds(5));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(0)]
    public void Issue_rejects_a_hash_that_is_not_32_bytes(int length) =>
        Should.Throw<ArgumentException>(() => HubTicket.Issue(Guid.NewGuid(), new byte[length], Lifetime, Now));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Issue_rejects_a_lifetime_that_is_not_positive(int seconds) =>
        Should.Throw<ArgumentOutOfRangeException>(() => HubTicket.Issue(Guid.NewGuid(), Hash(1), TimeSpan.FromSeconds(seconds), Now));
}
