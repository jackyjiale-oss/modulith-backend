using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TemplateName.Infrastructure.Common.Caching;
using TemplateName.Modules.Auth;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Infrastructure.External;
using TemplateName.Modules.Auth.Infrastructure.Tokens;

namespace TemplateName.UnitTests.Auth.External;

/// <summary>The replay guard as the module registers it: HybridCache in memory, on the application's (fake) clock.</summary>
public sealed class ExternalProofReplayGuardTests
{
    private const string Nonce = "nonce-0123456789abcdef";

    [Fact]
    public async Task The_same_nonce_for_one_provider_is_refused_the_second_time()
    {
        using var host = new Host();

        (await host.ClaimAsync("google", Nonce)).ShouldBeTrue();
        (await host.ClaimAsync("google", Nonce)).ShouldBeFalse();
        (await host.ClaimAsync("google", Nonce)).ShouldBeFalse();
    }

    [Fact]
    public async Task Another_provider_or_another_nonce_is_its_own_claim()
    {
        using var host = new Host();

        (await host.ClaimAsync("google", Nonce)).ShouldBeTrue();
        (await host.ClaimAsync("apple", Nonce)).ShouldBeTrue();
        (await host.ClaimAsync("google", Nonce + "-other")).ShouldBeTrue();
        (await host.ClaimAsync("google", Nonce.ToUpperInvariant())).ShouldBeTrue();
    }

    [Fact]
    public async Task A_claim_is_held_for_max_token_age_plus_clock_skew_and_then_freed()
    {
        using var host = new Host();
        (await host.ClaimAsync("google", Nonce)).ShouldBeTrue();

        // A token whose iat is up to the skew in the future stays young enough for MaxTokenAge after that iat, so the claim covers both.
        host.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30) - TimeSpan.FromMilliseconds(1));
        (await host.ClaimAsync("google", Nonce)).ShouldBeFalse();

        host.Time.Advance(TimeSpan.FromMilliseconds(1));
        (await host.ClaimAsync("google", Nonce)).ShouldBeTrue();
        (await host.ClaimAsync("google", Nonce)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_window_follows_the_configured_max_token_age_and_skew()
    {
        using var host = new Host(maxTokenAge: "00:10:00", clockSkew: "00:00:00");
        (await host.ClaimAsync("google", Nonce)).ShouldBeTrue();

        host.Time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromMilliseconds(1));
        (await host.ClaimAsync("google", Nonce)).ShouldBeFalse();

        host.Time.Advance(TimeSpan.FromMilliseconds(1));
        (await host.ClaimAsync("google", Nonce)).ShouldBeTrue();
    }

    [Fact]
    public async Task Caching_provider_none_does_not_switch_the_guard_off()
    {
        // "none" disables both cache layers for every entry without flags of its own; a spent nonce is a security record, not a cache entry.
        using var host = new Host(cachingProvider: "none");

        (await host.ClaimAsync("google", Nonce)).ShouldBeTrue();
        (await host.ClaimAsync("google", Nonce)).ShouldBeFalse();
    }

    [Fact]
    public async Task Concurrent_claims_of_one_nonce_let_exactly_one_through()
    {
        using var host = new Host();

        var claims = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => host.ClaimAsync("google", Nonce))));

        claims.Count(claimed => claimed).ShouldBe(1);
    }

    [Fact]
    public void The_key_is_the_hex_sha256_of_provider_and_nonce_under_auth_ext_nonce()
    {
        var expected = "auth:ext-nonce:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("google|" + Nonce)));

        var key = HybridCacheExternalProofReplayGuard.Key("google", Nonce);

        key.ShouldBe(expected);
        key.ShouldNotContain(Nonce);
    }

    private sealed class Host : IDisposable
    {
        private readonly ServiceProvider _services;

        public Host(string maxTokenAge = "00:05:00", string clockSkew = "00:00:30", string cachingProvider = "memory")
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:External:MaxTokenAge"] = maxTokenAge,
                ["Auth:Jwt:ClockSkew"] = clockSkew,
                ["Caching:Provider"] = cachingProvider,
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCaching(configuration);
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<IConfiguration>(configuration);
            services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.SectionName));
            AuthModule.AddPermissions(services, configuration);
            AuthModule.AddExternalLogin(services, configuration);
            _services = services.BuildServiceProvider();
            Guard = _services.GetRequiredService<IExternalProofReplayGuard>();
        }

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2031, 3, 1, 9, 0, 0, TimeSpan.Zero));

        public IExternalProofReplayGuard Guard { get; }

        public Task<bool> ClaimAsync(string provider, string nonce) => Guard.TryClaimAsync(provider, nonce, TestContext.Current.CancellationToken);

        public void Dispose() => _services.Dispose();
    }
}
