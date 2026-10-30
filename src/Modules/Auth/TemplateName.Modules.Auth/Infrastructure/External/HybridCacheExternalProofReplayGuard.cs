using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Infrastructure.Tokens;

namespace TemplateName.Modules.Auth.Infrastructure.External;

/// <summary>
/// Spends a provider proof's nonce in <see cref="HybridCache"/> under <c>auth:ext-nonce:{sha256(provider|nonce)}</c> (the nonce itself is
/// never a key, so it never shows in Redis). The first <c>GetOrCreateAsync</c> runs the factory and is the claim; a later one finds the entry.
/// An entry lives for <c>Auth:External:MaxTokenAge</c> plus <c>Auth:Jwt:ClockSkew</c>: a token whose <c>iat</c> is up to the skew in the
/// future stays young enough that long. Best effort (spec D9): in memory per instance, shared once Redis is the second level, and two
/// instances racing on Redis may both win. The entry options carry their own flags, so <c>Caching:Provider = none</c> (which switches both
/// layers off only for entries without flags) does not switch the guard off: a spent nonce is a security record, not a cache entry.
/// </summary>
internal sealed class HybridCacheExternalProofReplayGuard(
    HybridCache cache,
    IOptions<ExternalLoginOptions> externalOptions,
    IOptions<JwtOptions> jwtOptions) : IExternalProofReplayGuard
{
    private const string KeyPrefix = "auth:ext-nonce:";

    private readonly HybridCacheEntryOptions _entryOptions = EntryOptions(externalOptions.Value.MaxTokenAge + jwtOptions.Value.ClockSkew);

    /// <summary>The cache key of a claim. A provider name never contains <c>|</c>, so two pairs never share a key.</summary>
    internal static string Key(string provider, string nonce) =>
        KeyPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(provider + "|" + nonce)));

    public async Task<bool> TryClaimAsync(string provider, string nonce, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(provider);
        ArgumentException.ThrowIfNullOrEmpty(nonce);

        // Concurrent calls for one key share one factory run (HybridCache's stampede protection), so exactly one of them sees its flag set.
        var claimed = new ClaimFlag();
        await cache.GetOrCreateAsync(
            Key(provider, nonce),
            claimed,
            static (state, _) =>
            {
                state.Value = true;
                return ValueTask.FromResult(true);
            },
            _entryOptions,
            cancellationToken: cancellationToken);
        return claimed.Value;
    }

    private static HybridCacheEntryOptions EntryOptions(TimeSpan lifetime) => new()
    {
        Expiration = lifetime,
        LocalCacheExpiration = lifetime,

        // Explicit, never inherited from the defaults: both layers on, whatever Caching:Provider says.
        Flags = HybridCacheEntryFlags.None,
    };

    private sealed class ClaimFlag
    {
        public bool Value { get; set; }
    }
}
