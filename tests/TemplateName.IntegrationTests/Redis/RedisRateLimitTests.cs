using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using TemplateName.IntegrationTests.Infrastructure;

namespace TemplateName.IntegrationTests.Redis;

/// <summary>
/// The rate limiters with <c>Caching:Provider = redis</c> (spec 3.3, D5): two hosts (two API instances) on one Valkey share one fixed
/// window per policy and partition, under <c>{InstanceName}rl:{policy}:{partitionKey}</c>; with Valkey stopped each host limits in memory
/// on its own, without waiting for Redis. Every test uses its own <c>Caching:Redis:InstanceName</c>, so no count leaks between tests.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class RedisRateLimitTests(IntegrationTestWebAppFactory factory, ValkeyFixture valkey) : IntegrationTestBase(factory)
{
    // Anonymous: the limiter counts it, then authorization answers 401. Signed in: 404.
    private const string UnknownRoute = "/api/v1/does-not-exist";
    private const string LoginRoute = "/api/v1/auth/login";
    private const string Address = "203.0.113.10";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    // The command timeout (500 ms by default) plus room for a busy test machine. A request that waited on Redis (a queued command, a
    // timeout noticed on the next heartbeat) takes a second or more.
    private static readonly TimeSpan RequestCeiling = TimeSpan.FromSeconds(1);

    private readonly string _instanceName = $"ratelimit-{Guid.NewGuid():N}:";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await valkey.StartAsync(Ct);
    }

    [Fact]
    public async Task Two_hosts_share_one_count_and_the_next_request_on_either_gets_429_with_Retry_After_of_at_least_one()
    {
        await using var hostA = await CreateHostAsync(("RateLimiting:GlobalPermitLimit", "3"));
        await using var hostB = await CreateHostAsync(("RateLimiting:GlobalPermitLimit", "3"));
        using var clientA = hostA.CreateClient();
        using var clientB = hostB.CreateClient();

        (await StatusAsync(clientA, Address)).ShouldBe(HttpStatusCode.Unauthorized);
        (await StatusAsync(clientB, Address)).ShouldBe(HttpStatusCode.Unauthorized);
        (await StatusAsync(clientA, Address)).ShouldBe(HttpStatusCode.Unauthorized);
        using var refusedOnB = await SendAsync(clientB, Address);
        using var refusedOnA = await SendAsync(clientA, Address);

        foreach (var refused in new[] { refusedOnB, refusedOnA })
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            var retryAfter = refused.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldNotBeNull();
            retryAfter.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
            retryAfter.ShouldBeLessThanOrEqualTo(Window);
        }

        (await CountAsync(hostA, $"global:ip:{Address}")).ShouldBe(5);
    }

    [Fact]
    public async Task Window_rolls_over_after_the_window()
    {
        (string, string)[] settings = [("RateLimiting:GlobalPermitLimit", "1"), ("RateLimiting:GlobalWindow", "00:00:01")];
        await using var hostA = await CreateHostAsync(settings);
        await using var hostB = await CreateHostAsync(settings);
        using var clientA = hostA.CreateClient();
        using var clientB = hostB.CreateClient();

        (await StatusAsync(clientA, Address)).ShouldBe(HttpStatusCode.Unauthorized);
        using (var refused = await SendAsync(clientB, Address))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            refused.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(1));
        }

        // Real time: the window is Valkey's key expiry. Once the key is gone a new window starts on either host.
        var database = Database(hostA);
        await RedisTestHosts.EventuallyAsync(async () => !await database.KeyExistsAsync(Key($"global:ip:{Address}")), "the window to end", Ct);

        (await StatusAsync(clientB, Address)).ShouldBe(HttpStatusCode.Unauthorized);
        (await StatusAsync(clientA, Address)).ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_key_that_lost_its_expiry_is_repaired()
    {
        await using var host = await CreateHostAsync(("RateLimiting:GlobalPermitLimit", "3"));
        using var client = host.CreateClient();
        var database = Database(host);
        var key = Key($"global:ip:{Address}");
        await database.StringSetAsync(key, 1);
        (await database.KeyTimeToLiveAsync(key)).ShouldBeNull();

        (await StatusAsync(client, Address)).ShouldBe(HttpStatusCode.Unauthorized);

        var timeToLive = (await database.KeyTimeToLiveAsync(key)).ShouldNotBeNull();
        timeToLive.ShouldBeGreaterThan(TimeSpan.Zero);
        timeToLive.ShouldBeLessThanOrEqualTo(Window);
        (await CountAsync(host, $"global:ip:{Address}")).ShouldBe(2);
    }

    [Fact]
    public async Task Policies_do_not_share_counters()
    {
        (string, string)[] settings = [("RateLimiting:GlobalPermitLimit", "5"), ("RateLimiting:AuthStrictPermitLimit", "1")];
        await using var hostA = await CreateHostAsync(settings);
        await using var hostB = await CreateHostAsync(settings);
        using var clientA = hostA.CreateClient();
        using var clientB = hostB.CreateClient();

        (await LoginStatusAsync(clientA, Address)).ShouldNotBe(HttpStatusCode.TooManyRequests);
        (await LoginStatusAsync(clientB, Address)).ShouldBe(HttpStatusCode.TooManyRequests);

        // The same address still has global permits left: auth-strict's exhausted window is its own.
        (await StatusAsync(clientA, Address)).ShouldBe(HttpStatusCode.Unauthorized);
        (await CountAsync(hostA, $"global:ip:{Address}")).ShouldBe(3);
        (await CountAsync(hostA, $"auth-strict:ip:{Address}")).ShouldBe(2);
    }

    [Fact]
    public async Task IPv6_and_user_partition_keys_work()
    {
        await using var hostA = await CreateHostAsync(("RateLimiting:GlobalPermitLimit", "2"));
        await using var hostB = await CreateHostAsync(("RateLimiting:GlobalPermitLimit", "2"));
        using var clientA = hostA.CreateClient();
        using var clientB = hostB.CreateClient();
        var userId = Guid.NewGuid();

        // Each host signs with its own key, so each gets a token it issued itself, for the same user.
        var tokenA = TestAccessTokens.Issue(hostA.Services, userId).Value;
        var tokenB = TestAccessTokens.Issue(hostB.Services, userId).Value;

        (await StatusAsync(clientA, "::1")).ShouldBe(HttpStatusCode.Unauthorized);
        (await StatusAsync(clientB, "::1")).ShouldBe(HttpStatusCode.Unauthorized);
        (await StatusAsync(clientA, "::1")).ShouldBe(HttpStatusCode.TooManyRequests);

        (await StatusAsync(clientB, Address, tokenB)).ShouldBe(HttpStatusCode.NotFound);
        (await StatusAsync(clientA, Address, tokenA)).ShouldBe(HttpStatusCode.NotFound);
        (await StatusAsync(clientB, Address, tokenB)).ShouldBe(HttpStatusCode.TooManyRequests);

        (await CountAsync(hostA, "global:ip:::1")).ShouldBe(3);
        (await CountAsync(hostA, $"global:user:{userId}")).ShouldBe(3);
        (await Database(hostA).KeyExistsAsync(Key($"global:ip:{Address}"))).ShouldBeFalse();
    }

    [Fact]
    public async Task Valkey_stopped_each_host_limits_in_memory_and_requests_are_not_slowed_beyond_the_command_timeout()
    {
        // A threshold the test never reaches keeps the circuit closed, so every request really tries the stopped Valkey first.
        (string, string)[] settings = [("RateLimiting:GlobalPermitLimit", "3"), ("Caching:Redis:FailureThreshold", "100")];
        await using var hostA = await CreateHostAsync(settings);
        await using var hostB = await CreateHostAsync(settings);
        using var clientA = hostA.CreateClient();
        using var clientB = hostB.CreateClient();

        await valkey.StopAsync(Ct);
        try
        {
            await RedisTestHosts.WaitUntilDisconnectedAsync(hostA, Ct);
            await RedisTestHosts.WaitUntilDisconnectedAsync(hostB, Ct);

            // Each instance allows the whole limit on its own: instances x limit while Redis is down.
            foreach (var client in new[] { clientA, clientB })
            {
                for (var request = 0; request < 3; request++)
                {
                    (await TimedSendAsync(client)).ShouldBe(HttpStatusCode.Unauthorized);
                }

                (await TimedSendAsync(client)).ShouldBe(HttpStatusCode.TooManyRequests);
            }
        }
        finally
        {
            await valkey.StartAsync(Ct);
        }
    }

    private async Task<HttpStatusCode> TimedSendAsync(HttpClient client)
    {
        var elapsed = Stopwatch.StartNew();
        using var response = await SendAsync(client, Address);
        elapsed.Stop();

        elapsed.Elapsed.ShouldBeLessThan(RequestCeiling);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            response.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        }

        return response.StatusCode;
    }

    /// <summary>A host on this test's Valkey prefix, connected before it is used, so its first request does not race the connect.</summary>
    private async Task<WebApplicationFactory<Program>> CreateHostAsync(params (string Key, string Value)[] settings)
    {
        var host = RedisTestHosts.Create(Factory, valkey, [("Caching:Redis:InstanceName", _instanceName), .. settings]);
        await RedisTestHosts.WaitUntilConnectedAsync(host, Ct);
        return host;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string address, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UnknownRoute);
        request.Headers.Add(TestClientAddressStartupFilter.HeaderName, address);
        return await client.SendAsync(token is null ? request : request.WithBearer(token), Ct);
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string address, string? token = null)
    {
        using var response = await SendAsync(client, address, token);
        return response.StatusCode;
    }

    private static async Task<HttpStatusCode> LoginStatusAsync(HttpClient client, string address)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, LoginRoute)
        {
            Content = JsonContent.Create(new { email = "nobody@example.com", password = "not-the-password" }),
        };
        request.Headers.Add(TestClientAddressStartupFilter.HeaderName, address);
        using var response = await client.SendAsync(request, Ct);
        return response.StatusCode;
    }

    private RedisKey Key(string policyAndPartition) => $"{_instanceName}rl:{policyAndPartition}";

    private static IDatabase Database(WebApplicationFactory<Program> host)
        => host.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();

    private async Task<long> CountAsync(WebApplicationFactory<Program> host, string policyAndPartition)
        => (long)await Database(host).StringGetAsync(Key(policyAndPartition));
}
