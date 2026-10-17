using TemplateName.Modules.Notifications.Domain.Preferences;

namespace TemplateName.UnitTests.Notifications;

public sealed class QuietHoursTests
{
    private static readonly QuietHours Night = QuietHours.Create(new TimeOnly(22, 0), new TimeOnly(7, 0)).Value;

    // The IANA ids are looked up on purpose: a runtime that cannot find them must fail these tests, not skip them.
    private static TimeZoneInfo NewYork => TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static TimeZoneInfo KualaLumpur => TimeZoneInfo.FindSystemTimeZoneById("Asia/Kuala_Lumpur");

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void QuietHours_create_rejects_equal_start_and_end()
    {
        var result = QuietHours.Create(new TimeOnly(22, 0), new TimeOnly(22, 0));

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(PreferenceErrors.InvalidQuietHours);
    }

    [Fact]
    public void QuietHours_create_keeps_a_window_that_crosses_midnight_and_one_that_does_not()
    {
        var overnight = QuietHours.Create(new TimeOnly(22, 0), new TimeOnly(7, 0));
        var daytime = QuietHours.Create(new TimeOnly(13, 0), new TimeOnly(14, 0));

        overnight.IsSuccess.ShouldBeTrue();
        overnight.Value.ShouldBe(new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0)));
        daytime.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(23, 30, true)]
    [InlineData(22, 0, true)]
    [InlineData(0, 0, true)]
    [InlineData(6, 59, true)]
    [InlineData(7, 0, false)]
    [InlineData(21, 59, false)]
    [InlineData(12, 0, false)]
    public void Contains_handles_a_window_across_midnight(int hour, int minute, bool expected) =>
        Night.Contains(new TimeOnly(hour, minute)).ShouldBe(expected);

    [Theory]
    [InlineData(13, 0, true)]
    [InlineData(13, 59, true)]
    [InlineData(14, 0, false)]
    [InlineData(12, 59, false)]
    [InlineData(0, 0, false)]
    public void Contains_handles_a_window_within_one_day(int hour, int minute, bool expected) =>
        new QuietHours(new TimeOnly(13, 0), new TimeOnly(14, 0)).Contains(new TimeOnly(hour, minute)).ShouldBe(expected);

    [Fact]
    public void NextAllowedAt_returns_now_outside_the_window()
    {
        var now = Utc(2026, 10, 9, 8, 0); // 16:00 in Kuala Lumpur

        Night.NextAllowedAt(now, KualaLumpur).ShouldBe(now);
    }

    [Fact]
    public void NextAllowedAt_in_Kuala_Lumpur_returns_seven_local_as_utc()
    {
        // 23:30 MYT on 2026-03-01 is inside 22:00-07:00; the window ends at 07:00 MYT on 2026-03-02, which is 23:00 UTC on 2026-03-01.
        var now = Utc(2026, 3, 1, 15, 30);

        var next = Night.NextAllowedAt(now, KualaLumpur);

        next.ShouldBe(Utc(2026, 3, 1, 23, 0));
        next.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void NextAllowedAt_after_midnight_ends_the_same_local_morning()
    {
        // 04:00 MYT on 2026-03-02 (20:00 UTC on 2026-03-01): the window ends at 07:00 MYT the same day.
        var now = Utc(2026, 3, 1, 20, 0);

        Night.NextAllowedAt(now, KualaLumpur).ShouldBe(Utc(2026, 3, 1, 23, 0));
    }

    [Fact]
    public void NextAllowedAt_at_the_window_end_returns_now()
    {
        // 07:00 MYT is outside [22:00, 07:00).
        var now = Utc(2026, 3, 1, 23, 0);

        Night.NextAllowedAt(now, KualaLumpur).ShouldBe(now);
    }

    [Fact]
    public void NextAllowedAt_in_Utc_uses_the_utc_clock()
    {
        Night.NextAllowedAt(Utc(2026, 10, 9, 23, 30), TimeZoneInfo.Utc).ShouldBe(Utc(2026, 10, 10, 7, 0));
        Night.NextAllowedAt(Utc(2026, 10, 10, 3, 0), TimeZoneInfo.Utc).ShouldBe(Utc(2026, 10, 10, 7, 0));
    }

    [Fact]
    public void NextAllowedAt_across_spring_forward_in_New_York()
    {
        // 2026-03-08: at 02:00 EST the clocks jump to 03:00 EDT, so 02:30 does not exist. The window 01:00-02:30 ends at the
        // first valid instant after the gap, 03:00 EDT = 07:00 UTC. 01:30 EST is 06:30 UTC.
        var window = QuietHours.Create(new TimeOnly(1, 0), new TimeOnly(2, 30)).Value;
        var now = Utc(2026, 3, 8, 6, 30);

        var next = window.NextAllowedAt(now, NewYork);

        next.ShouldBe(Utc(2026, 3, 8, 7, 0));
        TimeZoneInfo.ConvertTime(next, NewYork).ToString("yyyy-MM-dd HH:mm zzz").ShouldBe("2026-03-08 03:00 -04:00");
    }

    [Fact]
    public void NextAllowedAt_on_the_fall_back_day_uses_the_earlier_offset()
    {
        // 2026-11-01: at 02:00 EDT the clocks return to 01:00 EST, so 01:30 happens twice. The window 22:00-01:30 ends at the earlier
        // one, 01:30 EDT = 05:30 UTC (the later one would be 06:30 UTC). 23:00 EDT on 2026-10-31 is 03:00 UTC.
        var window = QuietHours.Create(new TimeOnly(22, 0), new TimeOnly(1, 30)).Value;
        var now = Utc(2026, 11, 1, 3, 0);

        window.NextAllowedAt(now, NewYork).ShouldBe(Utc(2026, 11, 1, 5, 30));
    }

    [Fact]
    public void NextAllowedAt_in_the_first_ambiguous_hour_still_ends_at_the_earlier_offset()
    {
        // 01:10 EDT (05:10 UTC) is before the earlier 01:30.
        var window = QuietHours.Create(new TimeOnly(22, 0), new TimeOnly(1, 30)).Value;

        window.NextAllowedAt(Utc(2026, 11, 1, 5, 10), NewYork).ShouldBe(Utc(2026, 11, 1, 5, 30));
    }

    [Fact]
    public void NextAllowedAt_in_the_second_ambiguous_hour_never_returns_an_instant_in_the_past()
    {
        // 01:10 EST (06:10 UTC) is the second time 01:10 happens. The earlier 01:30 (05:30 UTC) has passed, so the window ends at the
        // later 01:30 EST = 06:30 UTC.
        var window = QuietHours.Create(new TimeOnly(22, 0), new TimeOnly(1, 30)).Value;
        var now = Utc(2026, 11, 1, 6, 10);

        var next = window.NextAllowedAt(now, NewYork);

        next.ShouldBe(Utc(2026, 11, 1, 6, 30));
        next.ShouldBeGreaterThan(now);
    }

    [Fact]
    public void NextAllowedAt_outside_the_window_on_a_transition_day_returns_now()
    {
        var window = QuietHours.Create(new TimeOnly(1, 0), new TimeOnly(2, 30)).Value;
        var now = Utc(2026, 3, 8, 12, 0); // 08:00 EDT

        window.NextAllowedAt(now, NewYork).ShouldBe(now);
    }
}
