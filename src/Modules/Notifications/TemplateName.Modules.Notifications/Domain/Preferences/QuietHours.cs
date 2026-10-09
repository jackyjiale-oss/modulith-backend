using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Domain.Preferences;

/// <summary>
/// A daily window in the user's local time during which non-critical email is held back: <c>[Start, End)</c>, crossing midnight when
/// <paramref name="End"/> is before <paramref name="Start"/> (22:00 to 07:00). Create it with <see cref="Create"/>, which refuses a
/// window whose start equals its end.
/// </summary>
/// <param name="Start">The first local time inside the window.</param>
/// <param name="End">The first local time after the window.</param>
internal sealed record QuietHours(TimeOnly Start, TimeOnly End)
{
    // Local wall-clock offsets run from UTC-12 to UTC+14; the search below brackets a gap with a margin beyond both.
    private static readonly TimeSpan MaxOffsetMargin = TimeSpan.FromHours(15);

    public static Result<QuietHours> Create(TimeOnly start, TimeOnly end) =>
        start == end
            ? Result.Failure<QuietHours>(PreferenceErrors.InvalidQuietHours)
            : new QuietHours(start, end);

    /// <summary>Whether <paramref name="localTime"/> is inside the window.</summary>
    public bool Contains(TimeOnly localTime) =>
        Start < End
            ? localTime >= Start && localTime < End
            : localTime >= Start || localTime < End;

    /// <summary>
    /// The earliest instant at or after <paramref name="now"/> at which email may be sent: <paramref name="now"/> itself outside the
    /// window, else the end of the current window converted to UTC. A local end that does not exist (it falls in a daylight-saving
    /// gap) becomes the first instant after the gap; one that happens twice (the clocks go back) takes the earlier occurrence, unless
    /// that has already passed, when the later one is taken. The result is never before <paramref name="now"/>.
    /// </summary>
    /// <param name="now">The current instant.</param>
    /// <param name="zone">The recipient's time zone.</param>
    public DateTimeOffset NextAllowedAt(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var localTime = TimeOnly.FromTimeSpan(local.TimeOfDay);

        if (!Contains(localTime))
        {
            return now;
        }

        // Inside the window the end is later today when the clock has not reached End yet (after midnight in a window that crosses it),
        // otherwise tomorrow.
        var endDate = localTime < End ? DateOnly.FromDateTime(local.DateTime) : DateOnly.FromDateTime(local.DateTime).AddDays(1);
        var localEnd = endDate.ToDateTime(End);

        return ToUtc(localEnd, zone, now);
    }

    private static DateTimeOffset ToUtc(DateTime localEnd, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (zone.IsInvalidTime(localEnd))
        {
            return FirstInstantAtOrAfter(localEnd, zone);
        }

        if (zone.IsAmbiguousTime(localEnd))
        {
            // The earlier instant has the larger offset (the clocks were ahead before they went back).
            var offsets = zone.GetAmbiguousTimeOffsets(localEnd).OrderByDescending(offset => offset).ToList();
            foreach (var offset in offsets)
            {
                var instant = new DateTimeOffset(localEnd, offset).ToUniversalTime();
                if (instant > now)
                {
                    return instant;
                }
            }

            return now;
        }

        return new DateTimeOffset(localEnd, zone.GetUtcOffset(localEnd)).ToUniversalTime();
    }

    // For a local time that does not exist: the earliest UTC instant whose local time has reached it, which is the end of the gap.
    // Local time only jumps forward across a gap, so "has reached it" is monotonic and a bisection finds the instant to the tick.
    private static DateTimeOffset FirstInstantAtOrAfter(DateTime localTime, TimeZoneInfo zone)
    {
        var asUtc = DateTime.SpecifyKind(localTime, DateTimeKind.Utc);
        var low = new DateTimeOffset(asUtc - MaxOffsetMargin, TimeSpan.Zero);
        var high = new DateTimeOffset(asUtc + MaxOffsetMargin, TimeSpan.Zero);

        while (high - low > TimeSpan.FromTicks(1))
        {
            var middle = low + ((high - low) / 2);
            if (TimeZoneInfo.ConvertTime(middle, zone).DateTime >= localTime)
            {
                high = middle;
            }
            else
            {
                low = middle;
            }
        }

        return high;
    }
}
