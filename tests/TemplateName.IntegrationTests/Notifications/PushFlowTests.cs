using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Domain.PushDevices;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Notifications;

/// <summary>
/// The push channel end to end: a host with <c>Notifications:Push:Enabled</c> (the shared factory keeps the template's default, off),
/// the real Auth sign-in, outbox, scheduler and delivery worker, and a recording <see cref="IPushTransport"/> in place of FCM.
/// </summary>
public sealed class PushFlowTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string Email = "push-flow@example.com";
    private const string LoginRoute = "/api/v1/auth/login";
    private const string DevicesRoute = "/api/v1/notifications/devices";

    private readonly RecordingPushTransport _push = new();

    private int _addressCount;

    [Fact]
    public async Task A_new_sign_in_pushes_to_the_earlier_devices_only()
    {
        await using var host = PushHost();
        await CreateUserAsync(Email);
        var laptop = await SignInOnDeviceAsync(host, "Laptop");
        await RegisterDeviceAsync(host, laptop, "token-laptop");
        var phone = await SignInOnDeviceAsync(host, "Phone");
        await RegisterDeviceAsync(host, phone, "token-phone");

        await DispatchNotificationsAsync(host.Services);

        var push = _push.Sent.ShouldHaveSingleItem();
        push.Token.ShouldBe("token-laptop");
        push.Data["typeCode"].ShouldBe("auth.new_device_login");
        push.Urgent.ShouldBeTrue();
    }

    [Fact]
    public async Task After_logout_the_device_gets_no_push_and_is_deactivated()
    {
        await using var host = PushHost();
        await CreateUserAsync(Email);
        var laptop = await SignInOnDeviceAsync(host, "Laptop");
        await RegisterDeviceAsync(host, laptop, "token-laptop");
        var tablet = await SignInOnDeviceAsync(host, "Tablet");
        await RegisterDeviceAsync(host, tablet, "token-tablet");
        await DispatchNotificationsAsync(host.Services);
        _push.Clear();

        await LogoutAsync(host, laptop);
        await SignInOnDeviceAsync(host, "Phone");
        await DispatchNotificationsAsync(host.Services);

        _push.Sent.Select(message => message.Token).ShouldBe(["token-tablet"]);
        var laptopDevice = await DeviceAsync("token-laptop");
        laptopDevice.IsActive.ShouldBeFalse();
        laptopDevice.DeactivationReason.ShouldBe(PushDeviceDeactivationReason.SessionEnded);
    }

    [Fact]
    public async Task Re_registering_after_signing_in_again_receives_the_next_alert()
    {
        await using var host = PushHost();
        await CreateUserAsync(Email);
        var first = await SignInOnDeviceAsync(host, "Laptop");
        await RegisterDeviceAsync(host, first, "token-laptop");
        await LogoutAsync(host, first);
        var again = await SignInOnDeviceAsync(host, "Laptop");
        await RegisterDeviceAsync(host, again, "token-laptop");

        await SignInOnDeviceAsync(host, "Phone");
        await DispatchNotificationsAsync(host.Services);

        _push.Sent.Select(message => message.Token).ShouldBe(["token-laptop"]);
        (await DeviceAsync("token-laptop")).SessionId.ShouldBe(again.SessionId);
    }

    [Fact]
    public async Task An_unregistered_answer_deactivates_the_device()
    {
        await using var host = PushHost();
        await CreateUserAsync(Email);
        var laptop = await SignInOnDeviceAsync(host, "Laptop");
        await RegisterDeviceAsync(host, laptop, "token-laptop");
        _push.FailNextWith(new PushSendException(isPermanent: true, "fcm_unregistered", deactivateDevice: true));

        await SignInOnDeviceAsync(host, "Phone");
        await DispatchNotificationsAsync(host.Services);

        var device = await DeviceAsync("token-laptop");
        device.DeactivationReason.ShouldBe(PushDeviceDeactivationReason.RejectedByProvider);
        var delivery = await QueryAsync(context => context.Set<Delivery>().SingleAsync(row => row.Channel == NotificationChannel.Push, Ct));
        delivery.Status.ShouldBe(DeliveryStatus.DeadLettered);
        delivery.LastError.ShouldBe("fcm_unregistered");
    }

    [Fact]
    public async Task Switching_push_off_in_preferences_stops_it()
    {
        await using var host = PushHost();
        await CreateUserAsync(Email);
        var laptop = await SignInOnDeviceAsync(host, "Laptop");
        await RegisterDeviceAsync(host, laptop, "token-laptop");
        using (var client = Authorized(host, laptop))
        {
            using var response = await client.PutAsJsonAsync(
                "/api/v1/notifications/preferences",
                new { items = new[] { new { typeCode = "auth.new_device_login", channel = "Push", enabled = false } } },
                Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await SignInOnDeviceAsync(host, "Phone");
        await DispatchNotificationsAsync(host.Services);

        _push.Sent.ShouldBeEmpty();
        (await QueryAsync(context => context.Set<Delivery>().CountAsync(row => row.Channel == NotificationChannel.Email, Ct))).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Account_lock_pushes_to_signed_in_devices()
    {
        await using var host = PushHost();
        await CreateUserAsync(Email);
        var laptop = await SignInOnDeviceAsync(host, "Laptop");
        await RegisterDeviceAsync(host, laptop, "token-laptop");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var refused = await PostAnonymousAsync(host, LoginRoute, new { email = Email, password = "wrong-password-123", deviceName = "Attacker" });
            refused.IsSuccessStatusCode.ShouldBeFalse();
        }

        await DispatchNotificationsAsync(host.Services);

        _push.Sent.ShouldContain(message => message.Token == "token-laptop" && message.Data["typeCode"] == "auth.account_locked");
    }

    private WebApplicationFactory<Program> PushHost() => Factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Notifications:Push:Enabled", "true");
        builder.UseSetting("Notifications:Push:Fcm:ProjectId", "test-project");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPushTransport>();
            services.AddSingleton<IPushTransport>(_push);
        });
    });

    private async Task<Session> SignInOnDeviceAsync(WebApplicationFactory<Program> host, string deviceName)
    {
        using var response = await PostAnonymousAsync(host, LoginRoute, new { email = Email, password = AuthTestHarness.Password, deviceName });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return new Session(body.GetProperty("accessToken").GetString()!, body.GetProperty("sessionId").GetGuid());
    }

    private async Task RegisterDeviceAsync(WebApplicationFactory<Program> host, Session session, string token)
    {
        using var client = Authorized(host, session);
        using var response = await client.PostAsJsonAsync(DevicesRoute, new { token, platform = "Android" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task LogoutAsync(WebApplicationFactory<Program> host, Session session)
    {
        using var client = Authorized(host, session);
        using var response = await client.PostAsync("/api/v1/auth/logout", content: null, Ct);
        response.IsSuccessStatusCode.ShouldBeTrue();
    }

    private HttpClient Authorized(WebApplicationFactory<Program> host, Session session)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        client.DefaultRequestHeaders.Add(TestClientAddressStartupFilter.HeaderName, NextAddress());
        return client;
    }

    private async Task<HttpResponseMessage> PostAnonymousAsync(WebApplicationFactory<Program> host, string route, object body)
    {
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body) };
        request.Headers.Add(TestClientAddressStartupFilter.HeaderName, NextAddress());
        return await client.SendAsync(request, Ct);
    }

    private string NextAddress() => $"198.51.100.{Interlocked.Increment(ref _addressCount)}";

    private Task<PushDevice> DeviceAsync(string token)
        => QueryAsync(context => context.Set<PushDevice>().SingleAsync(device => device.TokenHash == PushDevice.HashOf(token), Ct));

    private async Task<T> QueryAsync<T>(Func<NotificationsDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>());
    }

    private sealed record Session(string AccessToken, Guid SessionId);
}
