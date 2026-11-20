using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TemplateName.IntegrationTests.Auth;
using TemplateName.IntegrationTests.Infrastructure;

namespace TemplateName.IntegrationTests.Notifications;

/// <summary><c>ETag</c> and <c>If-Match</c> on the notification preferences and quiet hours (ADR 0038).</summary>
public sealed class PreferenceEntityTagTests(IntegrationTestWebAppFactory factory) : AdminTestBase(factory)
{
    private const string PreferencesRoute = "/api/v1/notifications/preferences";
    private const string QuietHoursRoute = "/api/v1/notifications/preferences/quiet-hours";

    [Fact]
    public async Task Preferences_get_etag_is_accepted_and_replaced()
    {
        await SignInAsync();
        var read = await ReadETagAsync(PreferencesRoute);

        using var updated = await Client.PutJsonAsync(PreferencesRoute, PasswordChangedInApp(false), read, Ct);

        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var next = EntityTagRequests.ETagOf(updated);
        next.ShouldNotBe(read);
        (await ReadETagAsync(PreferencesRoute)).ShouldBe(next);
    }

    [Fact]
    public async Task Preferences_stale_etag_is_412_and_changes_nothing()
    {
        await SignInAsync();
        var read = await ReadETagAsync(PreferencesRoute);
        using (var first = await Client.PutJsonAsync(PreferencesRoute, PasswordChangedInApp(false), null, Ct))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var stale = await Client.PutJsonAsync(PreferencesRoute, PasswordChangedInApp(true), read, Ct);

        await AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency.precondition_failed");
        var after = await ReadETagAsync(PreferencesRoute);
        after.ShouldNotBe(read);
    }

    [Fact]
    public async Task Preferences_star_matches_weak_is_412_and_malformed_is_400()
    {
        await SignInAsync();
        var read = await ReadETagAsync(PreferencesRoute);

        using (var weak = await Client.PutJsonAsync(PreferencesRoute, PasswordChangedInApp(false), "W/" + read, Ct))
        {
            await AssertProblemAsync(weak, HttpStatusCode.PreconditionFailed, "concurrency.precondition_failed");
        }

        using (var malformed = await Client.PutJsonAsync(PreferencesRoute, PasswordChangedInApp(false), "not-quoted", Ct))
        {
            await AssertProblemAsync(malformed, HttpStatusCode.BadRequest, "request.malformed");
        }

        using var star = await Client.PutJsonAsync(PreferencesRoute, PasswordChangedInApp(false), "*", Ct);
        star.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Quiet_hours_without_a_settings_row_have_an_etag_that_an_update_accepts()
    {
        await SignInAsync();
        var read = await ReadETagAsync(QuietHoursRoute);

        using var updated = await Client.PutJsonAsync(QuietHoursRoute, new { start = "22:00", end = "07:00" }, read, Ct);

        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var next = EntityTagRequests.ETagOf(updated);
        next.ShouldNotBe(read);
        (await ReadETagAsync(QuietHoursRoute)).ShouldBe(next);
    }

    [Fact]
    public async Task Quiet_hours_stale_etag_is_412_and_changes_nothing()
    {
        await SignInAsync();
        var read = await ReadETagAsync(QuietHoursRoute);
        using (var first = await Client.PutJsonAsync(QuietHoursRoute, new { start = "22:00", end = "07:00" }, null, Ct))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var stale = await Client.PutJsonAsync(QuietHoursRoute, new { start = "21:00", end = "06:00" }, read, Ct);

        await AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency.precondition_failed");
        using var current = await Client.GetAsync(QuietHoursRoute, Ct);
        (await current.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("start").GetString().ShouldBe("22:00");
    }

    [Fact]
    public async Task Without_if_match_both_updates_behave_as_before()
    {
        await SignInAsync();

        using (var preferences = await Client.PutJsonAsync(PreferencesRoute, PasswordChangedInApp(false), null, Ct))
        {
            preferences.StatusCode.ShouldBe(HttpStatusCode.OK);
            EntityTagRequests.ETagOf(preferences).ShouldBe(await ReadETagAsync(PreferencesRoute));
        }

        using var quiet = await Client.PutJsonAsync(QuietHoursRoute, new { start = "22:00", end = "07:00" }, null, Ct);
        quiet.StatusCode.ShouldBe(HttpStatusCode.OK);
        EntityTagRequests.ETagOf(quiet).ShouldBe(await ReadETagAsync(QuietHoursRoute));
    }

    private static object PasswordChangedInApp(bool enabled)
        => new { items = new[] { new { typeCode = "auth.password_changed", channel = "InApp", enabled } } };

    private async Task<string> ReadETagAsync(string route)
    {
        using var response = await Client.GetAsync(route, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return EntityTagRequests.ETagOf(response);
    }
}
