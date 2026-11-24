using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Infrastructure.Persistence;
using TemplateName.Modules.Notifications.Application;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Notifications;

/// <summary>
/// The phone code end to end: a host with <c>Auth:Phone:Enabled</c> and <c>Notifications:Sms:Enabled</c> (the shared factory keeps
/// both off), the real Auth routes, outbox, scheduler and delivery worker, and a recording <see cref="ISmsTransport"/> in place of Twilio.
/// The code is read from the text the worker rendered, as the user would read it, which also proves the worker decrypted it for SMS.
/// </summary>
public sealed partial class SmsFlowTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string PhoneRoute = "/api/v1/auth/me/phone";
    private const string ConfirmRoute = "/api/v1/auth/me/phone/confirm";
    private const string MeRoute = "/api/v1/auth/me";
    private const string DeliveriesRoute = "/api/v1/admin/notifications/deliveries";
    private const string Number = "+60123456789";
    private const string MessageSid = "SM0123456789abcdef0123456789abcdef";

    private readonly RecordingSmsTransport _sms = new();

    [Fact]
    public async Task Request_sends_the_code_by_sms_and_confirm_verifies()
    {
        await using var host = SmsHost();
        await SignInAsync();
        using var client = await CreateClientAsync(host);

        (await client.PostAsJsonAsync(PhoneRoute, new { phoneNumber = "+60 12-345 6789" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await DispatchNotificationsAsync(host.Services);

        var text = _sms.Sent.ShouldHaveSingleItem();
        text.To.ShouldBe(Number);
        text.Body.ShouldContain("10 minutes");
        using var confirmed = await client.PostAsJsonAsync(ConfirmRoute, new { code = CodeIn(text.Body) }, Ct);
        confirmed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetMeAsync(client)).GetProperty("phoneNumber").GetString().ShouldBe(Number);
    }

    [Fact]
    public async Task A_wrong_code_then_the_right_one()
    {
        await using var host = SmsHost();
        await SignInAsync();
        using var client = await CreateClientAsync(host);
        (await client.PostAsJsonAsync(PhoneRoute, new { phoneNumber = Number }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await DispatchNotificationsAsync(host.Services);
        var code = CodeIn(_sms.Sent.ShouldHaveSingleItem().Body);

        using var wrong = await client.PostAsJsonAsync(ConfirmRoute, new { code = code == "000000" ? "000001" : "000000" }, Ct);
        using var right = await client.PostAsJsonAsync(ConfirmRoute, new { code }, Ct);

        wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await wrong.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("auth.invalid_code");
        right.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task An_expired_code_is_never_sent()
    {
        await using var host = SmsHost();
        await SignInAsync();
        using var client = await CreateClientAsync(host);
        (await client.PostAsJsonAsync(PhoneRoute, new { phoneNumber = Number }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // Auth publishes and Notifications schedules the text; then the worker is late by more than the code's life.
        await host.Services.GetRequiredService<OutboxDispatcher<AuthDbContext>>().ProcessBatchAsync(Ct);
        Factory.Time.Advance(TimeSpan.FromMinutes(11));
        await DispatchNotificationsAsync(host.Services);

        _sms.Attempts.ShouldBeEmpty();
        (await SmsDeliveryAsync()).Status.ShouldBe(DeliveryStatus.Expired);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_with_the_same_code()
    {
        await using var host = SmsHost();
        await SignInAsync();
        using var client = await CreateClientAsync(host);
        _sms.FailNextWith(new SmsSendException(isPermanent: false, "timeout"));
        (await client.PostAsJsonAsync(PhoneRoute, new { phoneNumber = Number }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await DispatchNotificationsAsync(host.Services);
        _sms.Sent.ShouldBeEmpty();
        (await SmsDeliveryAsync()).LastError.ShouldBe("timeout");

        Factory.Time.Advance(TimeSpan.FromMinutes(2));
        await DispatchNotificationsAsync(host.Services);

        _sms.Attempts.Count.ShouldBe(2);
        _sms.Attempts[1].Body.ShouldBe(_sms.Attempts[0].Body);
        using var confirmed = await client.PostAsJsonAsync(ConfirmRoute, new { code = CodeIn(_sms.Sent.ShouldHaveSingleItem().Body) }, Ct);
        confirmed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task The_admin_delivery_list_shows_the_sid()
    {
        await using var host = SmsHost();
        _sms.MessageId = MessageSid;
        await SignInAsync(NotificationsPermissions.DeliveryView);
        using var client = await CreateClientAsync(host);
        (await client.PostAsJsonAsync(PhoneRoute, new { phoneNumber = Number }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await DispatchNotificationsAsync(host.Services);

        (await SmsDeliveryAsync()).ProviderMessageId.ShouldBe(MessageSid);
        using var response = await client.GetAsync(DeliveriesRoute, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var item = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items").EnumerateArray()
            .Single(candidate => candidate.GetProperty("providerMessageId").ValueKind == JsonValueKind.String);
        item.GetProperty("providerMessageId").GetString().ShouldBe(MessageSid);
        item.GetProperty("destination").GetString().ShouldBe("********789");
    }

    private WebApplicationFactory<Program> SmsHost() => Factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Auth:Phone:Enabled", "true");
        builder.UseSetting("Notifications:Sms:Enabled", "true");
        builder.UseSetting("Notifications:Sms:Twilio:AccountSid", "ACtest-account-sid");
        builder.UseSetting("Notifications:Sms:Twilio:AuthToken", "test-auth-token");
        builder.UseSetting("Notifications:Sms:Twilio:From", "+15005550006");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISmsTransport>();
            services.AddSingleton<ISmsTransport>(_sms);
        });
    });

    private static string CodeIn(string text)
    {
        var match = SixDigits().Match(text);
        match.Success.ShouldBeTrue("the text holds a 6-digit code");
        return match.Value;
    }

    private async Task<JsonElement> GetMeAsync(HttpClient client)
    {
        using var response = await client.GetAsync(MeRoute, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<Delivery> SmsDeliveryAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Set<Delivery>().AsNoTracking()
            .SingleAsync(delivery => delivery.Channel == NotificationChannel.Sms, Ct);
    }

    [GeneratedRegex(@"\b[0-9]{6}\b")]
    private static partial Regex SixDigits();
}
