using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Notifications.Application.Devices;
using TemplateName.Modules.Notifications.Domain.PushDevices;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Notifications;

/// <summary>The push device routes under <c>/api/v1/notifications/devices</c> through the running host.</summary>
public sealed class PushDeviceEndpointsTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string DevicesRoute = "/api/v1/notifications/devices";

    [Fact]
    public async Task Register_returns_the_device_without_the_token()
    {
        var user = await SignInAsync();

        using var response = await Client.PostAsJsonAsync(
            DevicesRoute, new { token = "token-abc", platform = "Android", deviceName = "Pixel 9", appVersion = "1.2.3" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain("token-abc");
        var json = JsonDocument.Parse(body).RootElement;
        json.TryGetProperty("token", out _).ShouldBeFalse();
        json.GetProperty("platform").GetString().ShouldBe("Android");
        json.GetProperty("deviceName").GetString().ShouldBe("Pixel 9");
        var stored = await DeviceAsync(json.GetProperty("id").GetGuid());
        stored.UserId.ShouldBe(user.UserId);
        stored.SessionId.ShouldBe(user.SessionId);
    }

    [Fact]
    public async Task Register_twice_keeps_one_device()
    {
        await SignInAsync();

        var first = await RegisterAsync("token-twice");
        var second = await RegisterAsync("token-twice");

        second.ShouldBe(first);
        (await QueryAsync(context => context.Set<PushDevice>().CountAsync(Ct))).ShouldBe(1);
    }

    [Fact]
    public async Task List_returns_active_devices_newest_first()
    {
        await SignInAsync();
        var older = await RegisterAsync("token-older");
        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        var newer = await RegisterAsync("token-newer");
        var removed = await RegisterAsync("token-removed");
        (await Client.DeleteAsync($"{DevicesRoute}/{removed}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var list = await Client.GetFromJsonAsync<JsonElement>(DevicesRoute, Ct);

        list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldBe([newer, older]);
        list.ToString().ShouldNotContain("token-");
    }

    [Fact]
    public async Task Delete_of_another_users_device_is_404_device_not_found()
    {
        await SignInAsync();
        var theirs = await RegisterAsync("token-theirs");
        await SignInAsync();

        using var response = await Client.DeleteAsync($"{DevicesRoute}/{theirs}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("notifications.device_not_found");
        (await DeviceAsync(theirs)).IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task A_token_with_white_space_is_400_validation_failed()
    {
        await SignInAsync();

        using var response = await Client.PostAsJsonAsync(DevicesRoute, new { token = "two words", platform = "Ios" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("validation.failed");
        (await QueryAsync(context => context.Set<PushDevice>().CountAsync(Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Register_beyond_the_limit_is_429()
    {
        var limit = Factory.Services.GetRequiredService<IOptions<PushOptions>>().Value.RegistrationsPerMinute;
        await SignInAsync();
        for (var i = 0; i < limit; i++)
        {
            await RegisterAsync($"token-limit-{i}");
        }

        using var refused = await Client.PostAsJsonAsync(DevicesRoute, new { token = "token-over", platform = "Web" }, Ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("rate_limit.exceeded");
    }

    [Fact]
    public async Task Routes_require_a_token()
    {
        (await Client.PostAsJsonAsync(DevicesRoute, new { token = "t", platform = "Android" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Client.GetAsync(DevicesRoute, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Client.DeleteAsync($"{DevicesRoute}/{Guid.NewGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<Guid> RegisterAsync(string token)
    {
        using var response = await Client.PostAsJsonAsync(DevicesRoute, new { token, platform = "Android" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private Task<PushDevice> DeviceAsync(Guid id) => QueryAsync(context => context.Set<PushDevice>().SingleAsync(device => device.Id == id, Ct));

    private async Task<T> QueryAsync<T>(Func<NotificationsDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>());
    }
}
