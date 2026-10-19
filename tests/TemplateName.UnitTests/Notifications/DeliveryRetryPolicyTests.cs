using TemplateName.Modules.Notifications.Domain.Deliveries;

namespace TemplateName.UnitTests.Notifications;

public sealed class DeliveryRetryPolicyTests
{
    private static readonly DateTime UtcNow = new(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(1, 5, 1)]
    [InlineData(2, 5, 5)]
    [InlineData(3, 5, 15)]
    [InlineData(4, 5, 60)]
    [InlineData(5, 6, 360)]
    [InlineData(9, 10, 360)]
    public void The_next_attempt_follows_the_schedule_and_clamps_at_six_hours(int failedAttempts, int maxAttempts, int expectedMinutes) =>
        DeliveryRetryPolicy.NextAttemptAt(failedAttempts, UtcNow, maxAttempts).ShouldBe(UtcNow.AddMinutes(expectedMinutes));

    [Theory]
    [InlineData(5, 5)]
    [InlineData(6, 5)]
    [InlineData(1, 1)]
    public void There_is_no_next_attempt_once_the_maximum_is_reached(int failedAttempts, int maxAttempts) =>
        DeliveryRetryPolicy.NextAttemptAt(failedAttempts, UtcNow, maxAttempts).ShouldBeNull();

    [Fact]
    public void The_result_keeps_the_utc_kind()
    {
        var next = DeliveryRetryPolicy.NextAttemptAt(1, UtcNow, 5);

        next!.Value.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-1, 5)]
    [InlineData(1, 0)]
    public void Non_positive_counts_are_rejected(int failedAttempts, int maxAttempts) =>
        Should.Throw<ArgumentOutOfRangeException>(() => DeliveryRetryPolicy.NextAttemptAt(failedAttempts, UtcNow, maxAttempts));
}
