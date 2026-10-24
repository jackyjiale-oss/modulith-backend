using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using TemplateName.Modules.Notifications;
using TemplateName.Modules.Notifications.Endpoints;
using TemplateName.Web.Common;
using TemplateName.Web.Common.Security;

namespace TemplateName.UnitTests.Web;

/// <summary>
/// <see cref="IFixedWindowLimiterFactory"/>: the in-memory default behaves as the built-in fixed window always did, and the three
/// fixed-window policies (<c>global</c>, <c>auth-strict</c>, <c>notifications-hub-tickets</c>) get their limiters from whatever factory
/// is registered, so Platform.Redis can swap in its own.
/// </summary>
public sealed class FixedWindowLimiterFactoryTests
{
    private const string AddressHeader = "Test-Remote-Address";
    private const string SubjectHeader = "Test-Subject";
    private const string Address = "203.0.113.7";
    private const string Subject = "5d0c9a43-2b1e-4f7a-9c3d-8e6f1a2b3c4d";

    [Fact]
    public async Task InMemory_factory_allows_the_limit_then_refuses_with_retry_after()
    {
        var window = TimeSpan.FromSeconds(30);
        using var limiter = new InMemoryFixedWindowLimiterFactory().Create("global", "ip:203.0.113.7", 2, window);

        using var first = limiter.AttemptAcquire();
        using var second = await limiter.AcquireAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var refused = limiter.AttemptAcquire();

        first.IsAcquired.ShouldBeTrue();
        second.IsAcquired.ShouldBeTrue();
        refused.IsAcquired.ShouldBeFalse();
        refused.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter).ShouldBeTrue();
        retryAfter.ShouldBe(window);

        // No queue: an asynchronous acquire beyond the limit is refused at once instead of waiting for the next window.
        var queued = limiter.AcquireAsync(cancellationToken: TestContext.Current.CancellationToken);
        queued.IsCompleted.ShouldBeTrue();
        using var queuedLease = await queued;
        queuedLease.IsAcquired.ShouldBeFalse();

        // The window replenishes by itself, as the built-in fixed window of today does.
        var fixedWindow = limiter.ShouldBeOfType<FixedWindowRateLimiter>();
        fixedWindow.IsAutoReplenishing.ShouldBeTrue();
        fixedWindow.ReplenishmentPeriod.ShouldBe(window);
    }

    [Fact]
    public async Task Policies_ask_the_factory_for_their_limiter()
    {
        var factory = Substitute.For<IFixedWindowLimiterFactory>();
        factory.Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<TimeSpan>())
            .Returns(_ => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions { PermitLimit = 100, Window = TimeSpan.FromMinutes(1) }));
        using var host = await StartHostAsync(factory);
        using var client = host.GetTestClient();

        (await SendAsync(client, "/anonymous", subject: null)).ShouldBe(HttpStatusCode.OK);
        (await SendAsync(client, "/strict", subject: null)).ShouldBe(HttpStatusCode.OK);
        (await SendAsync(client, "/tickets", Subject)).ShouldBe(HttpStatusCode.OK);

        factory.Received(1).Create("global", $"ip:{Address}", 300, TimeSpan.FromMinutes(1));
        factory.Received(1).Create("auth-strict", $"ip:{Address}", 10, TimeSpan.FromMinutes(1));
        factory.Received(1).Create("global", $"user:{Subject}", 300, TimeSpan.FromMinutes(1));
        factory.Received(1).Create("notifications-hub-tickets", $"user:{Subject}", 30, TimeSpan.FromMinutes(1));
        factory.ReceivedCalls().Count().ShouldBe(4);
    }

    [Fact]
    public void AddHttpSecurity_registers_the_in_memory_factory_unless_one_is_registered()
    {
        var configuration = new ConfigurationBuilder().Build();
        var replacement = Substitute.For<IFixedWindowLimiterFactory>();

        using var defaults = new ServiceCollection().AddHttpSecurity(configuration).BuildServiceProvider();
        using var replaced = new ServiceCollection().AddSingleton(replacement).AddHttpSecurity(configuration).BuildServiceProvider();

        defaults.GetRequiredService<IFixedWindowLimiterFactory>().ShouldBeOfType<InMemoryFixedWindowLimiterFactory>();
        replaced.GetRequiredService<IFixedWindowLimiterFactory>().ShouldBeSameAs(replacement);
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, string path, string? subject)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(AddressHeader, Address);
        if (subject is not null)
        {
            request.Headers.Add(SubjectHeader, subject);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static Task<IHost> StartHostAsync(IFixedWindowLimiterFactory factory)
        => new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices((context, services) =>
                {
                    // Registered first: AddHttpSecurity adds the in-memory factory only when none is registered.
                    services.AddSingleton(factory);
                    services.AddRouting().AddWebCommon().AddHttpSecurity(context.Configuration);
                    NotificationsModule.AddHub(services, context.Configuration);
                })
                .Configure(app =>
                {
                    // TestServer gives no remote address and no user; take both from headers so the partition keys are known.
                    app.Use((context, next) =>
                    {
                        context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers[AddressHeader].ToString());
                        if (context.Request.Headers.TryGetValue(SubjectHeader, out var subject))
                        {
                            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject.ToString())], "Test"));
                        }

                        return next(context);
                    });
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/anonymous", () => Results.Ok());
                        endpoints.MapGet("/strict", () => Results.Ok()).RequireRateLimiting(RateLimitPolicies.AuthStrict);
                        endpoints.MapGet("/tickets", () => Results.Ok()).RequireRateLimiting(HubTicketRateLimit.PolicyName);
                    });
                }))
            .StartAsync(TestContext.Current.CancellationToken);
}
