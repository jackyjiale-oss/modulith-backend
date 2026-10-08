using TemplateName.Infrastructure.Common.Outbox;

namespace TemplateName.UnitTests.Infrastructure;

public sealed class OutboxRetryPolicyTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 30)]
    [InlineData(3, 120)]
    [InlineData(4, 600)]
    [InlineData(5, 3600)]
    public void Delay_grows_per_attempt(int attempt, int seconds)
        => OutboxRetryPolicy.NextAttemptAt(attempt, T0, maxAttempts: 6).ShouldBe(T0.AddSeconds(seconds));

    [Fact]
    public void Returns_null_when_attempts_exhausted() => OutboxRetryPolicy.NextAttemptAt(5, T0, 5).ShouldBeNull();
}
