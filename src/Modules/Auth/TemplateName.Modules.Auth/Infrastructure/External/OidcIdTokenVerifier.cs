using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.Tokens;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Infrastructure.External;

/// <summary>
/// Verifies an OpenID Connect ID token for a configured provider (spec 5.2). The provider's metadata and signing keys come from
/// <c>{Authority}/.well-known/openid-configuration</c> through the <c>oidc-discovery</c> client, cached per provider by a
/// <see cref="ConfigurationManager{T}"/> (which re-reads them every 12 hours). A token whose <c>kid</c> is unknown, or metadata without
/// signing keys, makes the verifier read the metadata again inside the same request and validate once more (refresh, then retry), at most
/// once per the manager's <see cref="BaseConfigurationManager.RefreshInterval"/> (5 minutes) per provider, so a token under a freshly rotated
/// key passes and random <c>kid</c>s cannot make us hammer the provider. Checks, in order: size and shape (before any request), the posted
/// nonce's length, Microsoft's <c>tid</c> against the configured tenant, then <c>RS256</c> or <c>ES256</c> only, the signature by the key
/// the <c>kid</c> names, <c>iss</c> exactly one of the provider's issuers (Microsoft's for the token's own <c>tid</c>), <c>aud</c> exactly one
/// of its client IDs, <c>exp</c> and <c>nbf</c> with <c>Auth:Jwt:ClockSkew</c>, <c>iat</c> no older than <c>Auth:External:MaxTokenAge</c>
/// and not in the future beyond the skew, the <c>nonce</c> equal to the posted nonce or its lower-case hex SHA-256, and the subject. Any bad
/// proof is <see cref="ExternalLoginErrors.InvalidToken"/>; metadata or keys that cannot be had are
/// <see cref="ExternalLoginErrors.ProviderUnavailable"/>. It never throws for an input (a cancelled caller still throws) and spends no nonce:
/// the caller claims the returned one.
/// </summary>
internal sealed partial class OidcIdTokenVerifier(
    IHttpClientFactory httpClientFactory,
    IOptions<JwtOptions> jwtOptions,
    IOptions<ExternalLoginOptions> externalOptions,
    TimeProvider timeProvider,
    ILogger<OidcIdTokenVerifier> logger)
{
    private const string MetadataPath = "/.well-known/openid-configuration";

    // The latest representable instant, in seconds since the epoch; a larger iat cannot be a time.
    private const long MaxUnixSeconds = 253_402_300_799;

    private static readonly Guid ConsumersTenantId = Guid.Parse(ExternalProviderPresets.ConsumersTenantId);

    private static readonly string[] Algorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256];

    // A duplicated claim is refused, so this reading and the library's can never see different values for one name.
    private static readonly JsonDocumentOptions PayloadOptions = new() { AllowDuplicateProperties = false, MaxDepth = 16 };

    private readonly ConcurrentDictionary<string, ProviderMetadata> _metadata = new(StringComparer.Ordinal);
    private readonly HttpClientFactoryDocumentRetriever _documents = new(httpClientFactory);
    private readonly JsonWebTokenHandler _handler = new();
    private readonly TimeSpan _clockSkew = jwtOptions.Value.ClockSkew;
    private readonly TimeSpan _maxTokenAge = externalOptions.Value.MaxTokenAge;

    /// <summary>
    /// Verifies <paramref name="idToken"/> for <paramref name="provider"/> against the posted <paramref name="nonce"/>. On success the result
    /// carries the identity and the token's own <c>nonce</c> claim, which the caller spends: a replay of this token carries that value
    /// whichever form of the nonce it posts.
    /// </summary>
    public async Task<Result<VerifiedIdToken>> VerifyAsync(ExternalProvider provider, string idToken, string? nonce, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (!IsAcceptableNonce(nonce) || !TryRead(idToken, out var token, out var payload))
        {
            return ExternalLoginErrors.InvalidToken;
        }

        using (payload)
        {
            var claims = payload.RootElement;
            if (!TryResolveIssuers(provider, claims, out var issuers, out var tenant))
            {
                return ExternalLoginErrors.InvalidToken;
            }

            var metadata = MetadataFor(provider);
            var manager = metadata.Manager;
            OpenIdConnectConfiguration configuration;
            try
            {
                configuration = await manager.GetConfigurationAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // The proof was not judged: a provider outage is never reported as a bad token.
                LogMetadataFailed(logger, provider.Name, (exception.InnerException ?? exception).GetType().Name);
                return ExternalLoginErrors.ProviderUnavailable;
            }

            var refreshed = false;
            if (configuration.SigningKeys.Count == 0)
            {
                // Metadata without keys (no jwks_uri, or an empty key set) cannot judge any token: read it again once, else unavailable.
                var refresh = await RefreshAsync(provider, metadata, manager, cancellationToken);
                if (refresh.Configuration is not { SigningKeys.Count: > 0 } withKeys)
                {
                    if (!refresh.Failed)
                    {
                        LogNoSigningKeys(logger, provider.Name);
                    }

                    return ExternalLoginErrors.ProviderUnavailable;
                }

                configuration = withKeys;
                refreshed = true;
            }

            var result = await ValidateAsync(token, Parameters(provider, issuers, configuration));
            if (!result.IsValid && !refreshed && result.Exception is SecurityTokenSignatureKeyNotFoundException)
            {
                // The provider may have rotated its keys: read them again and validate once more, inside this request.
                var refresh = await RefreshAsync(provider, metadata, manager, cancellationToken);
                if (refresh.Failed)
                {
                    return ExternalLoginErrors.ProviderUnavailable;
                }

                if (refresh.Configuration is { } fresh)
                {
                    result = await ValidateAsync(token, Parameters(provider, issuers, fresh));
                }
            }

            return result.IsValid ? Accept(provider, claims, issuers, tenant, nonce!) : ExternalLoginErrors.InvalidToken;
        }
    }

    /// <summary>
    /// The checks the library does not make (<c>aud</c> exactly, <c>iat</c>, the nonce, the subject), the exact <c>iss</c> again, then the
    /// email and the name.
    /// </summary>
    private Result<VerifiedIdToken> Accept(ExternalProvider provider, JsonElement claims, string[] issuers, Guid? tenant, string postedNonce)
    {
        // Defence in depth: the library checks iss against ValidIssuers today, but if a later change set TokenValidationParameters
        // .ConfigurationManager, IdentityModel would also accept the discovery document's issuer, a value a generic provider controls.
        if (!TryGetString(claims, "iss", out var issuer) || !issuers.Contains(issuer, StringComparer.Ordinal))
        {
            return ExternalLoginErrors.InvalidToken;
        }

        // Exact and case-sensitive: one aud value must equal a configured client ID (the library would also forgive a trailing slash).
        if (!AudienceMatches(claims, provider.ClientIds))
        {
            return ExternalLoginErrors.InvalidToken;
        }

        if (!TryGetIssuedAt(claims, out var issuedAt))
        {
            return ExternalLoginErrors.InvalidToken;
        }

        var now = timeProvider.GetUtcNow();
        if (issuedAt > now + _clockSkew || issuedAt < now - _maxTokenAge)
        {
            return ExternalLoginErrors.InvalidToken;
        }

        if (!TryGetString(claims, "nonce", out var tokenNonce) || !NonceMatches(tokenNonce, postedNonce))
        {
            return ExternalLoginErrors.InvalidToken;
        }

        if (!TryGetSubject(claims, tenant, out var subject))
        {
            return ExternalLoginErrors.InvalidToken;
        }

        var email = EmailOf(claims);
        var hasUsableEmail = email is not null && provider.TrustEmail && EmailRuleHolds(provider.EmailRule, claims);
        var name = TryGetString(claims, "name", out var rawName) && !string.IsNullOrWhiteSpace(rawName) ? rawName.Trim() : null;

        return new VerifiedIdToken(new VerifiedExternalIdentity(provider.Name, subject, email, hasUsableEmail, name), tokenNonce);
    }

    private async Task<TokenValidationResult> ValidateAsync(JsonWebToken token, TokenValidationParameters parameters)
    {
        try
        {
            return await _handler.ValidateTokenAsync(token, parameters);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new TokenValidationResult { IsValid = false, Exception = exception };
        }
    }

    /// <summary>
    /// Reads the provider's metadata again (once per refresh interval, shared by concurrent callers). <see cref="RefreshOutcome.Failed"/>
    /// when it could not be fetched; a null configuration when the interval has not passed since the last refresh.
    /// </summary>
    private async Task<RefreshOutcome> RefreshAsync(
        ExternalProvider provider,
        ProviderMetadata metadata,
        ConfigurationManager<OpenIdConnectConfiguration> seen,
        CancellationToken cancellationToken)
    {
        try
        {
            return new RefreshOutcome(await metadata.RefreshAsync(seen, timeProvider.GetUtcNow(), cancellationToken), Failed: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogMetadataFailed(logger, provider.Name, (exception.InnerException ?? exception).GetType().Name);
            return new RefreshOutcome(null, Failed: true);
        }
    }

    private TokenValidationParameters Parameters(ExternalProvider provider, string[] issuers, OpenIdConnectConfiguration configuration)
    {
        var keys = configuration.SigningKeys.ToArray();
        var clock = timeProvider;
        return new TokenValidationParameters
        {
            ValidAlgorithms = Algorithms,
            ValidateIssuer = true,
            ValidIssuers = issuers,
            ValidateAudience = true,
            ValidAudiences = provider.ClientIds,
            IgnoreTrailingSlashWhenValidatingAudience = false,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = _clockSkew,

            // As for our own access tokens (JwtValidation): the lifetime against the application's TimeProvider, and only the key the kid
            // names is tried, so a missing or unknown kid resolves no key (and asks for a refresh) instead of trying every key.
            LifetimeValidator = (notBefore, expires, _, parameters) => JwtValidation.ValidateLifetime(notBefore, expires, parameters.ClockSkew, clock),
            IssuerSigningKeyResolver = (_, _, keyId, _) => string.IsNullOrEmpty(keyId)
                ? []
                : keys.Where(key => string.Equals(key.KeyId, keyId, StringComparison.Ordinal)),
            TryAllIssuerSigningKeys = false,
        };
    }

    private ProviderMetadata MetadataFor(ExternalProvider provider)
    {
        var authority = provider.Authority
            ?? throw new InvalidOperationException($"The external provider '{provider.Name}' has no authority to read its metadata from.");
        var address = authority.TrimEnd('/') + MetadataPath;

        return _metadata.GetOrAdd(address, static (metadataAddress, documents) => new ProviderMetadata(metadataAddress, documents), _documents);
    }

    /// <summary>A token of at most <see cref="ExternalProof.MaxTokenLength"/> characters in the signed (three-part) compact form, whose payload is a JSON object.</summary>
    private bool TryRead(string? idToken, out JsonWebToken token, out JsonDocument payload)
    {
        token = null!;
        payload = null!;
        if (string.IsNullOrWhiteSpace(idToken) || idToken.Length > ExternalProof.MaxTokenLength || !_handler.CanReadToken(idToken))
        {
            return false;
        }

        try
        {
            token = new JsonWebToken(idToken);
            if (token.IsEncrypted)
            {
                return false;
            }

            payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.EncodedPayload), PayloadOptions);
            if (payload.RootElement.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            payload.Dispose();
            return false;
        }
        catch (Exception)
        {
            // Whatever the parser throws for a malformed input (SecurityTokenMalformedException, ArgumentException, FormatException,
            // JsonException, ...) is a bad proof, never an error of ours.
            payload?.Dispose();
            return false;
        }
    }

    private static bool IsAcceptableNonce(string? nonce) =>
        nonce is { Length: >= ExternalProof.MinNonceLength and <= ExternalProof.MaxNonceLength };

    /// <summary>
    /// The accepted issuers, and Microsoft's tenant. Microsoft's issuers carry <c>{tenantid}</c>, which stands for the token's own <c>tid</c>;
    /// that <c>tid</c> must be a GUID the configured tenant allows. Read before the signature is checked, which only ever refuses more.
    /// </summary>
    private static bool TryResolveIssuers(ExternalProvider provider, JsonElement claims, out string[] issuers, out Guid? tenant)
    {
        issuers = [];
        tenant = null;
        if (!IsMicrosoft(provider))
        {
            issuers = [.. provider.Issuers];
            return issuers.Length > 0;
        }

        if (!TryGetString(claims, "tid", out var tid) || !Guid.TryParseExact(tid, "D", out var tenantId) || !TenantAllowed(provider.TenantId, tenantId))
        {
            return false;
        }

        tenant = tenantId;
        issuers = [.. provider.Issuers.Select(issuer => issuer.Replace(ExternalProviderPresets.TenantPlaceholder, tid, StringComparison.Ordinal))];
        return true;
    }

    /// <summary><c>common</c> takes every tenant, <c>organizations</c> every tenant but personal accounts, <c>consumers</c> only them, a GUID only itself.</summary>
    private static bool TenantAllowed(string? configured, Guid tenant) => configured switch
    {
        null or ExternalProviderPresets.DefaultMicrosoftTenant => true,
        ExternalProviderPresets.OrganizationsTenant => tenant != ConsumersTenantId,
        ExternalProviderPresets.ConsumersTenant => tenant == ConsumersTenantId,
        _ => Guid.TryParseExact(configured, "D", out var only) && tenant == only,
    };

    private static bool IsMicrosoft(ExternalProvider provider) => provider.Name == ExternalProviderPresets.Microsoft;

    /// <summary>
    /// <c>sub</c>, except Microsoft's <c>{tid}:{oid}</c> (<paramref name="tenant"/> is its parsed <c>tid</c>; both GUIDs, written lower-case);
    /// at most 256 characters, never blank.
    /// </summary>
    private static bool TryGetSubject(JsonElement claims, Guid? tenant, out string subject)
    {
        subject = string.Empty;
        if (tenant is { } tenantId)
        {
            if (!TryGetString(claims, "oid", out var oid) || !Guid.TryParseExact(oid, "D", out var objectId))
            {
                return false;
            }

            subject = $"{tenantId:D}:{objectId:D}";
            return true;
        }

        if (!TryGetString(claims, "sub", out var sub) || string.IsNullOrWhiteSpace(sub) || sub.Length > UserExternalLogin.SubjectMaxLength)
        {
            return false;
        }

        subject = sub;
        return true;
    }

    /// <summary>The <c>aud</c> claim (a string or an array) holds at least one value equal, exactly, to a configured client ID.</summary>
    private static bool AudienceMatches(JsonElement claims, IReadOnlyList<string> clientIds)
    {
        if (!claims.TryGetProperty("aud", out var audience))
        {
            return false;
        }

        return audience.ValueKind switch
        {
            JsonValueKind.String => clientIds.Contains(audience.GetString()!, StringComparer.Ordinal),
            JsonValueKind.Array => audience.EnumerateArray()
                .Any(value => value.ValueKind == JsonValueKind.String && clientIds.Contains(value.GetString()!, StringComparer.Ordinal)),
            _ => false,
        };
    }

    /// <summary>The trimmed <c>email</c>, or null when it is absent, not a string, blank or longer than the column.</summary>
    private static string? EmailOf(JsonElement claims)
    {
        if (!TryGetString(claims, "email", out var raw))
        {
            return null;
        }

        var email = raw.Trim();
        return email.Length is > 0 and <= UserExternalLogin.EmailMaxLength ? email : null;
    }

    private static bool EmailRuleHolds(ExternalEmailRule rule, JsonElement claims) => rule switch
    {
        ExternalEmailRule.EmailVerifiedClaim => IsTrue(claims, "email_verified"),
        ExternalEmailRule.MicrosoftDomainVerified => IsTrue(claims, "xms_edov"),
        _ => false,
    };

    /// <summary>The claim is the boolean <c>true</c> or exactly the string <c>"true"</c> (Apple sends either).</summary>
    private static bool IsTrue(JsonElement claims, string name) =>
        claims.TryGetProperty(name, out var value)
        && (value.ValueKind == JsonValueKind.True
            || (value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), "true", StringComparison.Ordinal)));

    private static bool NonceMatches(string tokenNonce, string postedNonce) =>
        tokenNonce.Length > 0
        && (string.Equals(tokenNonce, postedNonce, StringComparison.Ordinal)
            || string.Equals(tokenNonce, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(postedNonce))), StringComparison.Ordinal));

    /// <summary>A whole number of seconds since the epoch, from 0 to the latest representable instant.</summary>
    private static bool TryGetIssuedAt(JsonElement claims, out DateTimeOffset issuedAt)
    {
        issuedAt = default;
        if (!claims.TryGetProperty("iat", out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var seconds) || seconds is < 0 or > MaxUnixSeconds)
        {
            return false;
        }

        issuedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }

    private static bool TryGetString(JsonElement claims, string name, out string value)
    {
        if (claims.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The metadata of external provider {Provider} could not be read ({ExceptionType}); the sign-in was refused as unavailable")]
    private static partial void LogMetadataFailed(ILogger logger, string provider, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The metadata of external provider {Provider} has no signing keys; the sign-in was refused as unavailable")]
    private static partial void LogNoSigningKeys(ILogger logger, string provider);

    private readonly record struct RefreshOutcome(OpenIdConnectConfiguration? Configuration, bool Failed);

    /// <summary>
    /// One provider's metadata: the current <see cref="ConfigurationManager{T}"/> and the in-request refresh. The library's own
    /// <c>RequestRefresh</c> only starts a background read, so a refresh here builds a new manager, whose first read is awaited, and swaps it
    /// in once it succeeded. Refreshes are serialized and at most one per <see cref="BaseConfigurationManager.RefreshInterval"/>, measured on
    /// the application's clock; the interval starts when a refresh is tried, so a failing provider is not retried sooner either.
    /// </summary>
    private sealed class ProviderMetadata(string address, IDocumentRetriever documents)
    {
        private readonly SemaphoreSlim _refreshLock = new(1, 1);
        private volatile ConfigurationManager<OpenIdConnectConfiguration> _manager = Create(address, documents);
        private DateTimeOffset? _lastRefresh;

        public ConfigurationManager<OpenIdConnectConfiguration> Manager => _manager;

        /// <summary>
        /// The configuration after a refresh: a fresh read when <paramref name="seen"/> is still current and the interval has passed, the
        /// current one when another caller has refreshed since <paramref name="seen"/>, null when the interval has not passed. Throws when
        /// the read fails (the current manager stays).
        /// </summary>
        public async Task<OpenIdConnectConfiguration?> RefreshAsync(
            ConfigurationManager<OpenIdConnectConfiguration> seen,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            await _refreshLock.WaitAsync(cancellationToken);
            try
            {
                if (!ReferenceEquals(_manager, seen))
                {
                    return await _manager.GetConfigurationAsync(cancellationToken);
                }

                if (_lastRefresh is { } last && now - last < seen.RefreshInterval)
                {
                    return null;
                }

                _lastRefresh = now;
                var fresh = Create(address, documents);
                var configuration = await fresh.GetConfigurationAsync(cancellationToken);
                _manager = fresh;
                return configuration;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private static ConfigurationManager<OpenIdConnectConfiguration> Create(string address, IDocumentRetriever documents) =>
            new(address, new OpenIdConnectConfigurationRetriever(), documents);
    }

    /// <summary>
    /// Reads each document with a client from the factory at the time of the request (so handler rotation and the <c>oidc-discovery</c>
    /// resilience pipeline apply), through <see cref="HttpDocumentRetriever"/>, which requires <c>https</c>.
    /// </summary>
    private sealed class HttpClientFactoryDocumentRetriever(IHttpClientFactory httpClientFactory) : IDocumentRetriever
    {
        public async Task<string> GetDocumentAsync(string address, CancellationToken cancellationToken)
        {
            using var client = httpClientFactory.CreateClient(ExternalHttpClients.OidcDiscovery);
            return await new HttpDocumentRetriever(client) { RequireHttps = true }.GetDocumentAsync(address, cancellationToken);
        }
    }
}

/// <summary>A verified ID token: the identity and the token's own <c>nonce</c> claim, which the caller spends. Prints its type name only.</summary>
internal sealed record VerifiedIdToken(VerifiedExternalIdentity Identity, string Nonce)
{
    /// <summary>The type name only: the generated <c>ToString</c> would print the nonce.</summary>
    public override string ToString() => nameof(VerifiedIdToken);
}
