using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Messaging;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Infrastructure.Persistence;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Scheduling;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Domain.Preferences;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;
using TemplateName.SharedKernel;

namespace TemplateName.IntegrationTests.Notifications;

/// <summary>
/// The preference and quiet-hours routes under <c>/api/v1/notifications/preferences</c> through the running host and the real database:
/// the defaults, all-or-nothing updates, the rule that a row is stored only when it differs from the default, concurrent updates of one
/// row, the <c>"HH:mm"</c> times, the Auth time zone, and that the stored choices are honoured by the scheduler.
/// </summary>
public sealed class PreferenceEndpointTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string PreferencesRoute = "/api/v1/notifications/preferences";
    private const string QuietHoursRoute = "/api/v1/notifications/preferences/quiet-hours";
    private const string PasswordChanged = "auth.password_changed";

    private DateTimeOffset Now => Factory.Time.GetUtcNow();

    [Fact]
    public async Task Defaults_show_every_type_with_mandatory_flags()
    {
        await SignInAsync();

        var body = await GetPreferencesAsync();

        body.EnumerateObject().Select(property => property.Name).ShouldBe(["types"]);
        var types = body.GetProperty("types").EnumerateArray().ToList();

        // Every catalog type, once, in the ordinal order of its code (the host also registers the delivery tests' types).
        var catalog = Factory.Services.GetRequiredService<NotificationCatalog>();
        types.Select(type => type.GetProperty("typeCode").GetString()).ShouldBe([.. catalog.All.Select(type => type.Code).Order(StringComparer.Ordinal)]);
        types.Count.ShouldBeGreaterThanOrEqualTo(8 + 3);

        foreach (var type in types)
        {
            type.EnumerateObject().Select(property => property.Name).ShouldBe(["typeCode", "category", "channels"], ignoreOrder: true);
            var definition = catalog.Find(type.GetProperty("typeCode").GetString()!)!;
            type.GetProperty("category").GetString().ShouldBe(definition.Category.ToString());

            var channels = type.GetProperty("channels").EnumerateArray().ToList();
            channels.Select(channel => channel.GetProperty("channel").GetString()).ShouldBe([.. definition.DefaultChannels.Order().Select(channel => channel.ToString())]);
            foreach (var channel in channels)
            {
                channel.EnumerateObject().Select(property => property.Name).ShouldBe(["channel", "enabled", "mandatory"], ignoreOrder: true);
                var kind = Enum.Parse<NotificationChannel>(channel.GetProperty("channel").GetString()!);
                channel.GetProperty("enabled").GetBoolean().ShouldBeTrue();
                channel.GetProperty("mandatory").GetBoolean().ShouldBe(definition.IsMandatory(kind));
            }
        }

        // The real content of the API: a security notice's email cannot be switched off, its in-app copy can.
        Channels(body, PasswordChanged).ShouldBe([("Email", true, true), ("InApp", true, false)]);
        Channels(body, "auth.token_reuse_detected").ShouldBe([("Email", true, true), ("InApp", true, false)]);
        Channels(body, "auth.account_locked").ShouldBe([("Email", true, true)]);
        Channels(body, "auth.password_reset").ShouldBe([("Email", true, true)]);
        types.Single(type => type.GetProperty("typeCode").GetString() == PasswordChanged).GetProperty("category").GetString().ShouldBe("Security");
        (await StoredAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Disabling_in_app_for_password_changed_is_stored_and_honoured()
    {
        var user = await SignInAsync();

        var updated = await PutAsync(Item(PasswordChanged, "InApp", false));

        Channels(updated, PasswordChanged).ShouldBe([("Email", true, true), ("InApp", false, false)]);
        (await StoredAsync()).ShouldBe([(user.UserId, PasswordChanged, NotificationChannel.InApp, false)]);
        Channels(await GetPreferencesAsync(), PasswordChanged).ShouldBe([("Email", true, true), ("InApp", false, false)]);

        // Published after the preference was stored: the scheduler honours it, and only the email delivery is created.
        await PublishAsync(new PasswordChangedIntegrationEvent(Guid.NewGuid(), Now, user.UserId));

        var notification = (await NotificationsAsync()).ShouldHaveSingleItem();
        notification.Deliveries.ShouldHaveSingleItem().Channel.ShouldBe(NotificationChannel.Email);
    }

    [Fact]
    public async Task Enabling_again_removes_the_row_and_the_in_app_delivery_returns()
    {
        var user = await SignInAsync();
        await PutAsync(Item(PasswordChanged, "InApp", false));
        (await StoredAsync()).ShouldHaveSingleItem();

        var updated = await PutAsync(Item(PasswordChanged, "InApp", true));

        // Rows exist only when they differ from the default (D10), so the row is gone.
        Channels(updated, PasswordChanged).ShouldBe([("Email", true, true), ("InApp", true, false)]);
        (await StoredAsync()).ShouldBeEmpty();

        await PublishAsync(new PasswordChangedIntegrationEvent(Guid.NewGuid(), Now, user.UserId));

        (await NotificationsAsync()).ShouldHaveSingleItem().Deliveries.Select(delivery => delivery.Channel)
            .ShouldBe([NotificationChannel.Email, NotificationChannel.InApp], ignoreOrder: true);
    }

    [Fact]
    public async Task Enabling_a_mandatory_channel_is_a_no_op_and_stores_nothing()
    {
        await SignInAsync();

        var updated = await PutAsync(Item(PasswordChanged, "Email", true));

        Channels(updated, PasswordChanged).ShouldBe([("Email", true, true), ("InApp", true, false)]);
        (await StoredAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_the_items_sent_change_and_the_rest_stay_as_they_were()
    {
        var user = await SignInAsync();
        await PutAsync(Item(PasswordChanged, "InApp", false));

        await PutAsync(Item("auth.token_reuse_detected", "InApp", false));

        (await StoredAsync()).ShouldBe(
            [
                (user.UserId, PasswordChanged, NotificationChannel.InApp, false),
                (user.UserId, "auth.token_reuse_detected", NotificationChannel.InApp, false),
            ],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Disabling_mandatory_email_is_rejected_and_nothing_changes()
    {
        await SignInAsync();

        // A valid item first: the request is all or nothing, so even it is not stored.
        using var response = await PutRawAsync(
            Item("auth.token_reuse_detected", "InApp", false),
            Item(PasswordChanged, "Email", false));

        var problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        problem.GetProperty("code").GetString().ShouldBe("notifications.channel_mandatory");
        problem.GetProperty("params").GetProperty("typeCode").GetString().ShouldBe(PasswordChanged);
        problem.GetProperty("params").GetProperty("channel").GetString().ShouldBe("Email");
        (await StoredAsync()).ShouldBeEmpty();
        (await GetPreferencesAsync()).GetProperty("types").EnumerateArray()
            .SelectMany(type => type.GetProperty("channels").EnumerateArray())
            .ShouldAllBe(channel => channel.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Unknown_type_and_unsupported_channel_are_rejected()
    {
        await SignInAsync();

        using var unknown = await PutRawAsync(Item("auth.password_changed", "InApp", false), Item("nope.nothing", "Email", false));
        var unknownProblem = await ProblemAsync(unknown, HttpStatusCode.BadRequest);
        unknownProblem.GetProperty("code").GetString().ShouldBe("notifications.unknown_type");
        unknownProblem.GetProperty("params").GetProperty("typeCode").GetString().ShouldBe("nope.nothing");

        // auth.registration_attempted is delivered by email only.
        using var unsupported = await PutRawAsync(Item("auth.password_changed", "InApp", false), Item("auth.registration_attempted", "InApp", false));
        var unsupportedProblem = await ProblemAsync(unsupported, HttpStatusCode.BadRequest);
        unsupportedProblem.GetProperty("code").GetString().ShouldBe("notifications.channel_not_supported");
        unsupportedProblem.GetProperty("params").GetProperty("typeCode").GetString().ShouldBe("auth.registration_attempted");
        unsupportedProblem.GetProperty("params").GetProperty("channel").GetString().ShouldBe("InApp");

        (await StoredAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Two_items_for_one_type_and_channel_are_applied_in_order_so_the_last_one_wins()
    {
        var user = await SignInAsync();

        await PutAsync(Item(PasswordChanged, "InApp", false), Item(PasswordChanged, "InApp", true));
        (await StoredAsync()).ShouldBeEmpty();

        await PutAsync(Item(PasswordChanged, "InApp", true), Item(PasswordChanged, "InApp", false));
        (await StoredAsync()).ShouldBe([(user.UserId, PasswordChanged, NotificationChannel.InApp, false)]);
    }

    [Fact]
    public async Task Preferences_belong_to_the_token_user_alone()
    {
        var alice = await SignInAsync();
        var bob = Guid.NewGuid();
        await InsertPreferenceAsync(bob, PasswordChanged, NotificationChannel.InApp);

        // Bob's stored choice is not Alice's.
        Channels(await GetPreferencesAsync(), PasswordChanged).ShouldBe([("Email", true, true), ("InApp", true, false)]);

        await PutAsync(Item(PasswordChanged, "InApp", false));
        await PutAsync(Item(PasswordChanged, "InApp", true));

        // Enabling again removed Alice's row only.
        (await StoredAsync()).ShouldBe([(bob, PasswordChanged, NotificationChannel.InApp, false)]);
        alice.UserId.ShouldNotBe(bob);
    }

    [Fact]
    public async Task A_user_id_in_the_query_or_the_body_is_ignored_by_the_preference_routes()
    {
        // The routes take the owner from the token alone: another user's id, wherever it is put, reads and changes the caller's data.
        var alice = await SignInAsync();
        var bob = Guid.NewGuid();
        await InsertPreferenceAsync(bob, PasswordChanged, NotificationChannel.InApp);
        await InsertQuietHoursAsync(bob, new TimeOnly(20, 0), new TimeOnly(8, 0));

        using (var read = await Client.GetAsync($"{PreferencesRoute}?userId={bob}", Ct))
        {
            read.StatusCode.ShouldBe(HttpStatusCode.OK);
            Channels(await read.Content.ReadFromJsonAsync<JsonElement>(Ct), PasswordChanged).ShouldBe([("Email", true, true), ("InApp", true, false)]);
        }

        using (var update = await Client.PutAsJsonAsync(
            $"{PreferencesRoute}?userId={bob}", new { userId = bob, items = new[] { Item(PasswordChanged, "InApp", false) } }, Ct))
        {
            update.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await StoredAsync()).ShouldBe(
            [(bob, PasswordChanged, NotificationChannel.InApp, false), (alice.UserId, PasswordChanged, NotificationChannel.InApp, false)],
            ignoreOrder: true);

        using (var readQuietHours = await Client.GetAsync($"{QuietHoursRoute}?userId={bob}", Ct))
        {
            (await ReadQuietHoursAsync(readQuietHours)).ShouldBe(new QuietHoursBody(null, null, "UTC"));
        }

        using (var updateQuietHours = await Client.PutAsync(
            $"{QuietHoursRoute}?userId={bob}", Json($$"""{"userId":"{{bob}}","start":"23:00","end":"06:00"}"""), Ct))
        {
            (await ReadQuietHoursAsync(updateQuietHours)).ShouldBe(new QuietHoursBody("23:00", "06:00", "UTC"));
        }

        (await ProfileRowsAsync()).ShouldBe(
            [(bob, new TimeOnly(20, 0), new TimeOnly(8, 0)), (alice.UserId, new TimeOnly(23, 0), new TimeOnly(6, 0))],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Quiet_hours_of_one_user_are_not_visible_to_or_changed_by_another()
    {
        var alice = await SignInAsync();
        (await PutQuietHoursAsync("""{"start":"22:00","end":"07:00"}""")).ShouldBe(new QuietHoursBody("22:00", "07:00", "UTC"));

        // Bob, signed in on the same client, sees no window and sets his own, then clears it; Alice's stays as she set it.
        var bob = await SignInAsync();
        (await GetQuietHoursAsync()).ShouldBe(new QuietHoursBody(null, null, "UTC"));
        (await PutQuietHoursAsync("""{"start":"01:00","end":"05:00"}""")).ShouldBe(new QuietHoursBody("01:00", "05:00", "UTC"));
        (await PutQuietHoursAsync("""{"start":null,"end":null}""")).ShouldBe(new QuietHoursBody(null, null, "UTC"));

        (await ProfileRowsAsync()).ShouldBe(
            [(alice.UserId, (TimeOnly?)new TimeOnly(22, 0), (TimeOnly?)new TimeOnly(7, 0)), (bob.UserId, (TimeOnly?)null, (TimeOnly?)null)],
            ignoreOrder: true);

        // Alice still reads her own window.
        AuthTestHarness.Authorize(Client, alice.AccessToken);
        (await GetQuietHoursAsync()).ShouldBe(new QuietHoursBody("22:00", "07:00", "UTC"));
    }

    [Fact]
    public async Task Concurrent_updates_of_the_same_item_do_not_fail()
    {
        var user = await SignInAsync();

        for (var round = 0; round < 5; round++)
        {
            // Both requests create the same row at once: one of them hits the primary key.
            var created = await Task.WhenAll(PutRawStatusAsync(Item(PasswordChanged, "InApp", false)), PutRawStatusAsync(Item(PasswordChanged, "InApp", false)));
            created.ShouldAllBe(status => status == HttpStatusCode.OK, $"round {round} (create)");
            (await StoredAsync()).ShouldBe([(user.UserId, PasswordChanged, NotificationChannel.InApp, false)]);

            // Both delete it at once: the second finds nothing left to delete.
            var removed = await Task.WhenAll(PutRawStatusAsync(Item(PasswordChanged, "InApp", true)), PutRawStatusAsync(Item(PasswordChanged, "InApp", true)));
            removed.ShouldAllBe(status => status == HttpStatusCode.OK, $"round {round} (remove)");
            (await StoredAsync()).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task Invalid_item_lists_are_validation_problems()
    {
        await SignInAsync();

        foreach (var count in new[] { 0, 101 })
        {
            using var response = await Client.PutAsJsonAsync(PreferencesRoute, new { items = Enumerable.Range(0, count).Select(index => Item($"test.type_{index}", "InApp", false)) }, Ct);
            var problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
            problem.GetProperty("code").GetString().ShouldBe("validation.failed");
            problem.GetProperty("errors").TryGetProperty("items", out _).ShouldBeTrue();
        }

        // 100 is the largest accepted list (each is an unknown type here, which is not a validation failure).
        using var maximum = await Client.PutAsJsonAsync(PreferencesRoute, new { items = Enumerable.Range(0, 100).Select(index => Item($"test.type_{index}", "InApp", false)) }, Ct);
        (await ProblemAsync(maximum, HttpStatusCode.BadRequest)).GetProperty("code").GetString().ShouldBe("notifications.unknown_type");

        using var missing = await Client.PutAsJsonAsync(PreferencesRoute, new { }, Ct);
        (await ProblemAsync(missing, HttpStatusCode.BadRequest)).GetProperty("code").GetString().ShouldBe("validation.failed");

        using var nullItem = await Client.PutAsync(PreferencesRoute, Json("""{"items":[null]}"""), Ct);
        (await ProblemAsync(nullItem, HttpStatusCode.BadRequest)).GetProperty("code").GetString().ShouldBe("validation.failed");

        using var blank = await Client.PutAsJsonAsync(PreferencesRoute, new { items = new[] { Item(" ", "InApp", false) } }, Ct);
        var blankProblem = await ProblemAsync(blank, HttpStatusCode.BadRequest);
        blankProblem.GetProperty("code").GetString().ShouldBe("validation.failed");
        blankProblem.GetProperty("errors").TryGetProperty("items[0].typeCode", out _).ShouldBeTrue();

        using var numeric = await Client.PutAsync(PreferencesRoute, Json("""{"items":[{"typeCode":"auth.password_changed","channel":99,"enabled":false}]}"""), Ct);
        var numericProblem = await ProblemAsync(numeric, HttpStatusCode.BadRequest);
        numericProblem.GetProperty("code").GetString().ShouldBe("validation.failed");
        numericProblem.GetProperty("errors").TryGetProperty("items[0].channel", out _).ShouldBeTrue();

        (await StoredAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{"items":[{"typeCode":"auth.password_changed","channel":"Sms","enabled":false}]}""")]
    [InlineData("""{"items":[{"typeCode":"auth.password_changed","channel":"InApp","enabled":"maybe"}]}""")]
    [InlineData("""{"items":"all"}""")]
    [InlineData("""{"items":[""")]
    public async Task Malformed_bodies_are_a_400_problem_not_a_500(string body)
    {
        await SignInAsync();

        using var response = await Client.PutAsync(PreferencesRoute, Json(body), Ct);

        var problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        problem.GetProperty("code").GetString().ShouldBe("request.malformed");
        (await StoredAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Quiet_hours_round_trip_and_clear()
    {
        var user = await SignInAsync();

        // No profile yet: no window, and the user's own (default) time zone.
        (await GetQuietHoursAsync()).ShouldBe(new QuietHoursBody(null, null, "UTC"));
        (await ProfileRowsAsync()).ShouldBeEmpty();

        var set = await PutQuietHoursAsync("""{"start":"23:30","end":"07:00"}""");
        set.ShouldBe(new QuietHoursBody("23:30", "07:00", "UTC"));
        (await GetQuietHoursAsync()).ShouldBe(set);
        (await ProfileRowsAsync()).ShouldBe([(user.UserId, new TimeOnly(23, 30), new TimeOnly(7, 0))]);

        // A window that does not cross midnight, replacing the first.
        (await PutQuietHoursAsync("""{"start":"01:00","end":"05:45"}""")).ShouldBe(new QuietHoursBody("01:00", "05:45", "UTC"));
        (await ProfileRowsAsync()).ShouldBe([(user.UserId, new TimeOnly(1, 0), new TimeOnly(5, 45))]);

        // Both null clears; the row stays with empty columns.
        (await PutQuietHoursAsync("""{"start":null,"end":null}""")).ShouldBe(new QuietHoursBody(null, null, "UTC"));
        (await GetQuietHoursAsync()).ShouldBe(new QuietHoursBody(null, null, "UTC"));
        (await ProfileRowsAsync()).ShouldBe([(user.UserId, (TimeOnly?)null, (TimeOnly?)null)]);

        // An empty body is a window without a start or an end: it clears too (a PUT replaces the whole value).
        await PutQuietHoursAsync("""{"start":"22:00","end":"06:00"}""");
        (await PutQuietHoursAsync("{}")).ShouldBe(new QuietHoursBody(null, null, "UTC"));
    }

    [Fact]
    public async Task Quiet_hours_response_has_exactly_the_documented_members()
    {
        await SignInAsync();

        using var response = await Client.PutAsync(QuietHoursRoute, Json("""{"start":"22:00","end":"07:00"}"""), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateObject().Select(property => property.Name)
            .ShouldBe(["start", "end", "timeZone"], ignoreOrder: true);
    }

    [Fact]
    public async Task Quiet_hours_response_shows_the_auth_time_zone()
    {
        var user = await SignInAsync();
        await PutQuietHoursAsync("""{"start":"22:00","end":"07:00"}""");

        // Changed where it belongs, through Auth's own profile route; this module only shows it.
        await ChangeAuthTimeZoneAsync("Asia/Kuala_Lumpur");
        (await GetQuietHoursAsync()).TimeZone.ShouldBe("Asia/Kuala_Lumpur");
        (await PutQuietHoursAsync("""{"start":"22:00","end":"07:00"}""")).TimeZone.ShouldBe("Asia/Kuala_Lumpur");

        await ChangeAuthTimeZoneAsync("America/New_York");
        (await GetQuietHoursAsync()).TimeZone.ShouldBe("America/New_York");

        // An empty id (the column cannot be null) is shown as UTC, the zone the scheduler uses for it.
        await SetStoredAuthTimeZoneAsync(user.UserId, string.Empty);
        (await GetQuietHoursAsync()).TimeZone.ShouldBe("UTC");
        await SetStoredAuthTimeZoneAsync(user.UserId, "Not/AZone");
        (await GetQuietHoursAsync()).TimeZone.ShouldBe("UTC");
    }

    [Theory]
    [InlineData("""{"start":"24:00","end":"07:00"}""")]
    [InlineData("""{"start":"22:00","end":"7"}""")]
    [InlineData("""{"start":"22:00","end":"07:60"}""")]
    [InlineData("""{"start":"22:00","end":"07:00:00"}""")]
    [InlineData("""{"start":"10pm","end":"07:00"}""")]
    [InlineData("""{"start":"","end":"07:00"}""")]
    [InlineData("""{"start":2200,"end":"07:00"}""")]
    public async Task Malformed_times_are_a_400_problem_not_a_500(string body)
    {
        await SignInAsync();

        using var response = await Client.PutAsync(QuietHoursRoute, Json(body), Ct);

        var problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        problem.GetProperty("code").GetString().ShouldBe("request.malformed");
        (await ProfileRowsAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{"start":"22:00","end":null}""")]
    [InlineData("""{"start":null,"end":"07:00"}""")]
    [InlineData("""{"start":"22:00"}""")]
    [InlineData("""{"start":"22:00","end":"22:00"}""")]
    [InlineData("""{"start":"00:00","end":"00:00"}""")]
    public async Task One_sided_or_equal_quiet_hours_are_rejected_and_nothing_changes(string body)
    {
        var user = await SignInAsync();
        await PutQuietHoursAsync("""{"start":"23:30","end":"07:00"}""");

        using var response = await Client.PutAsync(QuietHoursRoute, Json(body), Ct);

        var problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        problem.GetProperty("code").GetString().ShouldBe("notifications.invalid_quiet_hours");
        (await ProfileRowsAsync()).ShouldBe([(user.UserId, new TimeOnly(23, 30), new TimeOnly(7, 0))]);
    }

    [Fact]
    public async Task Quiet_hours_stored_through_the_api_defer_a_normal_email_but_not_in_app_or_a_security_notice()
    {
        var user = await SignInAsync();
        await PutQuietHoursAsync("""{"start":"22:00","end":"07:00"}""");

        // The clock starts at midnight UTC, inside the window, and the user's zone is UTC.
        var normalEmail = await ScheduleAsync(DeliveryTestTypeSource.Email, user.UserId);
        var inApp = await ScheduleAsync(DeliveryTestTypeSource.InApp, user.UserId);
        await PublishAsync(new PasswordChangedIntegrationEvent(Guid.NewGuid(), Now, user.UserId));

        normalEmail.NextAttemptAt.ShouldBe(Now.AddHours(7).UtcDateTime);
        inApp.NextAttemptAt.ShouldBe(Now.UtcDateTime);
        var securityEmail = (await NotificationsAsync()).Single(notification => notification.TypeCode == PasswordChanged)
            .Deliveries.Single(delivery => delivery.Channel == NotificationChannel.Email);
        securityEmail.NextAttemptAt.ShouldBe(Now.UtcDateTime);
    }

    [Fact]
    public async Task Every_route_requires_a_token()
    {
        // No SignInAsync: the client is anonymous.
        using var get = await Client.GetAsync(PreferencesRoute, Ct);
        using var put = await Client.PutAsJsonAsync(PreferencesRoute, new { items = new[] { Item(PasswordChanged, "InApp", false) } }, Ct);
        using var getQuietHours = await Client.GetAsync(QuietHoursRoute, Ct);
        using var putQuietHours = await Client.PutAsync(QuietHoursRoute, Json("""{"start":"22:00","end":"07:00"}"""), Ct);

        get.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        put.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        getQuietHours.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        putQuietHours.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await StoredAsync()).ShouldBeEmpty();
        (await ProfileRowsAsync()).ShouldBeEmpty();
    }

    private static object Item(string typeCode, string channel, bool enabled) => new { typeCode, channel, enabled };

    private static StringContent Json(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

    private static List<(string Channel, bool Enabled, bool Mandatory)> Channels(JsonElement preferences, string typeCode)
        => [.. preferences.GetProperty("types").EnumerateArray()
            .Single(type => type.GetProperty("typeCode").GetString() == typeCode)
            .GetProperty("channels").EnumerateArray()
            .Select(channel => (channel.GetProperty("channel").GetString()!, channel.GetProperty("enabled").GetBoolean(), channel.GetProperty("mandatory").GetBoolean()))];

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, body);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<JsonElement> GetPreferencesAsync()
    {
        using var response = await Client.GetAsync(PreferencesRoute, Ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToString());
        return body;
    }

    private async Task<JsonElement> PutAsync(params object[] items)
    {
        using var response = await PutRawAsync(items);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToString());
        return body;
    }

    private Task<HttpResponseMessage> PutRawAsync(params object[] items) => Client.PutAsJsonAsync(PreferencesRoute, new { items }, Ct);

    private async Task<HttpStatusCode> PutRawStatusAsync(params object[] items)
    {
        using var response = await PutRawAsync(items);
        return response.StatusCode;
    }

    private async Task<QuietHoursBody> GetQuietHoursAsync()
    {
        using var response = await Client.GetAsync(QuietHoursRoute, Ct);
        return await ReadQuietHoursAsync(response);
    }

    private async Task<QuietHoursBody> PutQuietHoursAsync(string json)
    {
        using var response = await Client.PutAsync(QuietHoursRoute, Json(json), Ct);
        return await ReadQuietHoursAsync(response);
    }

    private static async Task<QuietHoursBody> ReadQuietHoursAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToString());
        return new QuietHoursBody(
            body.GetProperty("start").GetString(),
            body.GetProperty("end").GetString(),
            body.GetProperty("timeZone").GetString()!);
    }

    private async Task ChangeAuthTimeZoneAsync(string timeZone)
    {
        using var response = await Client.PutAsJsonAsync("/api/v1/auth/me", new { displayName = "Test user", locale = "en", timeZone }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task SetStoredAuthTimeZoneAsync(Guid userId, string timeZone)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE auth.Users SET TimeZone = @TimeZone WHERE Id = @UserId",
            [new SqlParameter("TimeZone", timeZone), new SqlParameter("UserId", userId)],
            Ct);
    }

    private Task PublishAsync<TEvent>(TEvent integrationEvent)
        where TEvent : IIntegrationEvent
        => Factory.Services.GetRequiredService<IIntegrationEventPublisher>().PublishAsync(integrationEvent, Ct);

    private async Task<Delivery> ScheduleAsync(string typeCode, Guid userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var request = new NotificationRequest(
            typeCode,
            userId,
            Guid.NewGuid(),
            nameof(PreferenceEndpointTests),
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            EmailOverride: null,
            ExpiresAt: null);

        (await scope.ServiceProvider.GetRequiredService<NotificationScheduler>().ScheduleAsync(request, contactVariables: null, Ct))
            .ShouldBe(ScheduleOutcome.Scheduled);

        var notification = await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Set<Notification>()
            .Include(candidate => candidate.Deliveries)
            .AsNoTracking()
            .SingleAsync(candidate => candidate.SourceMessageId == request.SourceMessageId, Ct);

        return notification.Deliveries.ShouldHaveSingleItem();
    }

    private async Task<List<Notification>> NotificationsAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Set<Notification>()
            .Include(notification => notification.Deliveries)
            .AsNoTracking()
            .ToListAsync(Ct);
    }

    private async Task<List<(Guid UserId, string TypeCode, NotificationChannel Channel, bool IsEnabled)>> StoredAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Set<UserPreference>().AsNoTracking().ToListAsync(Ct);
        return [.. rows.Select(row => (row.UserId, row.TypeCode, row.Channel, row.IsEnabled))];
    }

    private async Task InsertPreferenceAsync(Guid userId, string typeCode, NotificationChannel channel)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        context.Add(UserPreference.Create(userId, typeCode, channel, isEnabled: false, Now));
        await context.SaveChangesAsync(Ct);
    }

    private async Task InsertQuietHoursAsync(Guid userId, TimeOnly start, TimeOnly end)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var profile = UserNotificationProfile.Create(userId, Now);
        profile.SetQuietHours(QuietHours.Create(start, end).Value, Now);
        context.Add(profile);
        await context.SaveChangesAsync(Ct);
    }

    private async Task<List<(Guid UserId, TimeOnly? Start, TimeOnly? End)>> ProfileRowsAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Set<UserNotificationProfile>().AsNoTracking().ToListAsync(Ct);
        return [.. rows.Select(row => (row.UserId, row.QuietHours?.Start, row.QuietHours?.End))];
    }

    private sealed record QuietHoursBody(string? Start, string? End, string TimeZone);
}
