using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Infrastructure.Tokens;

namespace TemplateName.Modules.Auth.Infrastructure.External;

/// <summary>
/// Spends a provider proof's nonce under <c>auth:ext-nonce:{sha256(provider|nonce)}</c> (the nonce itself is never a key, so it never shows in
/// Redis), for <c>Auth:External:MaxTokenAge</c> plus <c>Auth:Jwt:ClockSkew</c>: a token whose <c>iat</c> is up to the skew in the future stays
/// young enough that long. Two layers:
/// <list type="number">
/// <item><see cref="ExternalNonceGate"/>, in process: an atomic add-if-absent, so exactly one of any number of concurrent claims of a nonce
/// wins on this instance (HybridCache's stampede protection alone does not guarantee that: a claim arriving just after the factory finished
/// can run it again). Bounded; when it is full of live claims a new claim is refused (fail closed) and a warning is logged.</item>
/// <item><see cref="HybridCache"/>, across instances: the first <c>GetOrCreateAsync</c> runs the factory and is the claim; a later one, on any
/// instance sharing Redis, finds the entry. Best effort (spec D9): two instances racing through Redis may both win. Its entry options carry
/// their own flags, so <c>Caching:Provider = none</c> does not switch it off; the in-process gate holds regardless.</item>
/// </list>
/// </summary>
internal sealed partial class HybridCacheExternalProofReplayGuard(
    HybridCache cache,
    ExternalNonceGate gate,
    IOptions<ExternalLoginOptions> externalOptions,
    IOptions<JwtOptions> jwtOptions,
    ILogger<HybridCacheExternalProofReplayGuard> logger) : IExternalProofReplayGuard
{
    private const string KeyPrefix = "auth:ext-nonce:";

    private readonly TimeSpan _window = externalOptions.Value.MaxTokenAge + jwtOptions.Value.ClockSkew;
    private readonly HybridCacheEntryOptions _entryOptions = EntryOptions(externalOptions.Value.MaxTokenAge + jwtOptions.Value.ClockSkew);

    /// <summary>The key of a claim. A provider name never contains <c>|</c>, so two pairs never share a key.</summary>
    internal static string Key(string provider, string nonce) =>
        KeyPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(provider + "|" + nonce)));

    public async Task<bool> TryClaimAsync(string provider, string nonce, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(provider);
        ArgumentException.ThrowIfNullOrEmpty(nonce);

        var key = Key(provider, nonce);
        switch (gate.TryEnter(key, _window))
        {
            case NonceGateResult.Spent:
                return false;
            case NonceGateResult.Full:
                LogGateFull(logger, gate.Capacity);
                return false;
        }

        // This instance's only winner for the key; now the shared layer, which another instance may already hold.
        var claimed = new ClaimFlag();
        await cache.GetOrCreateAsync(
            key,
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "The social-login replay guard holds {Capacity} live nonces; a sign-in was refused until some expire")]
    private static partial void LogGateFull(ILogger logger, int capacity);

    private sealed class ClaimFlag
    {
        public bool Value { get; set; }
    }
}
