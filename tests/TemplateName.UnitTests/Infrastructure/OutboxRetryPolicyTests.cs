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

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 5)]
    [InlineData(3, 35)]
    [InlineData(4, 155)]
    [InlineData(5, 755)]
    [InlineData(6, 755 + 3600)]
    [InlineData(28, 755 + (23 * 3600))]
    [InlineData(29, 755 + (24 * 3600))]
    [InlineData(100, 755 + (95 * 3600))]
    public void Retry_window_is_the_sum_of_the_delays_between_the_attempts(int maxAttempts, int seconds)
        => OutboxRetryPolicy.RetryWindow(maxAttempts).ShouldBe(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void Default_attempts_retry_for_under_a_quarter_of_an_hour()
        => OutboxRetryPolicy.RetryWindow(new OutboxOptions().MaxAttempts).ShouldBe(TimeSpan.FromSeconds(755));
}
