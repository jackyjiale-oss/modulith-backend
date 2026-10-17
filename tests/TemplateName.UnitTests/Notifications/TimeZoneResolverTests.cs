using TemplateName.Modules.Notifications.Application.Catalog;

namespace TemplateName.UnitTests.Notifications;

public sealed class TimeZoneResolverTests
{
    [Fact]
    public void A_known_iana_id_is_found()
    {
        var (zone, isFallback) = TimeZoneResolver.Resolve("Asia/Kuala_Lumpur");

        isFallback.ShouldBeFalse();
        zone.BaseUtcOffset.ShouldBe(TimeSpan.FromHours(8));
    }

    [Fact]
    public void Utc_itself_is_not_a_fallback()
    {
        var (zone, isFallback) = TimeZoneResolver.Resolve("UTC");

        isFallback.ShouldBeFalse();
        zone.BaseUtcOffset.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("Mars/Olympus")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("../../etc/passwd")]
    public void An_unknown_blank_or_missing_id_falls_back_to_utc(string? id)
    {
        var (zone, isFallback) = TimeZoneResolver.Resolve(id);

        isFallback.ShouldBeTrue();
        zone.ShouldBe(TimeZoneInfo.Utc);
    }

    [Fact]
    public void A_windows_id_is_accepted_only_when_the_runtime_resolves_it()
    {
        // The Windows id of Singapore. Windows resolves it natively and Linux or macOS with ICU maps it to an IANA id; a runtime that
        // cannot resolve it must fall back to UTC and say so.
        var windowsId = "Singapore Standard Time";
        var runtimeResolves = TimeZoneInfo.TryFindSystemTimeZoneById(windowsId, out var expected);

        var (zone, isFallback) = TimeZoneResolver.Resolve(windowsId);

        if (runtimeResolves)
        {
            isFallback.ShouldBeFalse();
            zone.ShouldBe(expected!);
        }
        else
        {
            isFallback.ShouldBeTrue();
            zone.ShouldBe(TimeZoneInfo.Utc);
        }
    }
}
