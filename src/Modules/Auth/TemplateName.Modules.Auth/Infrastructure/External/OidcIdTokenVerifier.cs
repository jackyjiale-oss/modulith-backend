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
/// <c>{Authority}/.well-known/openid-configuration</c> through the <c>oidc-discovery</c> client, one <see cref="ConfigurationManager{T}"/> per
/// provider for the life of the process (refreshed every 12 hours, and in the background after a token names an unknown <c>kid</c>, at most
/// once per 5 minutes). Checks, in order: size and shape (before any request), the posted nonce's length, Microsoft's <c>tid</c> against the
/// configured tenant, then <c>RS256</c> or <c>ES256</c> only, the signature by the key the <c>kid</c> names, <c>iss</c> exactly one of the
/// provider's issuers (Microsoft's for the token's own <c>tid</c>), <c>aud</c> one of its client IDs, <c>exp</c> and <c>nbf</c> with
/// <c>Auth:Jwt:ClockSkew</c>, <c>iat</c> no older than <c>Auth:External:MaxTokenAge</c> and not in the future beyond the skew, the
/// <c>nonce</c> equal to the posted nonce or its lower-case hex SHA-256, and the subject. Any bad proof is
/// <see cref="ExternalLoginErrors.InvalidToken"/>; metadata that cannot be fetched is <see cref="ExternalLoginErrors.ProviderUnavailable"/>.
/// It never throws for an input (a cancelled caller still throws) and spends no nonce: the caller claims the returned one.
/// </summary>
internal sealed partial class OidcIdTokenVerifier(
    IHttpClientFactory httpClientFactory,
    IOptions<JwtOptions> jwtOptions,
    IOptions<ExternalLoginOptions> externalOptions,
    TimeProvider timeProvider,
    ILogger<OidcIdTokenVerifier> logger)
{
    private const string MetadataPath = "/.well-known/openid-configuration";
    private const string OrganizationsTenant = "organizations";
    private const string ConsumersTenantName = "consumers";

    /// <summary>The tenant of Microsoft's personal accounts.</summary>
    private static readonly Guid ConsumersTenant = Guid.Parse("9188040d-6c67-4c5b-b112-36a304b66dad");

    private static readonly string[] Algorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256];

    // A duplicated claim is refused, so this reading and the library's can never see different values for one name.
    private static readonly JsonDocumentOptions PayloadOptions = new() { AllowDuplicateProperties = false, MaxDepth = 16 };

    // The latest representable instant, in seconds since the epoch; a larger iat cannot be a time.
    private const long MaxUnixSeconds = 253_402_300_799;

    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _managers = new(StringComparer.Ordinal);
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
            if (!TryResolveIssuers(provider, claims, out var issuers))
            {
                return ExternalLoginErrors.InvalidToken;
            }

            var manager = ManagerFor(provider);
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
                LogDiscoveryFailed(logger, provider.Name, (exception.InnerException ?? exception).GetType().Name);
                return ExternalLoginErrors.ProviderUnavailable;
            }

            TokenValidationResult result;
            try
            {
                result = await _handler.ValidateTokenAsync(token, Parameters(provider, issuers, configuration));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return ExternalLoginErrors.InvalidToken;
            }

            if (!result.IsValid)
            {
                if (result.Exception is SecurityTokenSignatureKeyNotFoundException)
                {
                    // The provider may have rotated its keys: read them again in the background (throttled by the manager).
                    manager.RequestRefresh();
                }

                return ExternalLoginErrors.InvalidToken;
            }

            return Accept(provider, claims, issuers, nonce!);
        }
    }

    /// <summary>The checks the library does not make: the exact issuer again, <c>iat</c>, the nonce, the subject; then the email and name.</summary>
    private Result<VerifiedIdToken> Accept(ExternalProvider provider, JsonElement claims, string[] issuers, string postedNonce)
    {
        if (!TryGetString(claims, "iss", out var issuer) || !issuers.Contains(issuer, StringComparer.Ordinal))
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

        if (!TryGetSubject(provider, claims, out var subject))
        {
            return ExternalLoginErrors.InvalidToken;
        }

        var email = EmailOf(claims);
        var hasUsableEmail = email is not null && provider.TrustEmail && EmailRuleHolds(provider.EmailRule, claims);
        var name = TryGetString(claims, "name", out var rawName) && !string.IsNullOrWhiteSpace(rawName) ? rawName.Trim() : null;

        return new VerifiedIdToken(new VerifiedExternalIdentity(provider.Name, subject, email, hasUsableEmail, name), tokenNonce);
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

    private ConfigurationManager<OpenIdConnectConfiguration> ManagerFor(ExternalProvider provider)
    {
        var authority = provider.Authority
            ?? throw new InvalidOperationException($"The external provider '{provider.Name}' has no authority to read its metadata from.");
        var address = authority.TrimEnd('/') + MetadataPath;

        return _managers.GetOrAdd(
            address,
            static (metadataAddress, documents) =>
                new ConfigurationManager<OpenIdConnectConfiguration>(metadataAddress, new OpenIdConnectConfigurationRetriever(), documents),
            _documents);
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
    /// The accepted issuers. Microsoft's carry <c>{tenantid}</c>, which stands for the token's own <c>tid</c>; that <c>tid</c> must be a GUID
    /// the configured tenant allows. Read before the signature is checked, which only ever refuses more.
    /// </summary>
    private static bool TryResolveIssuers(ExternalProvider provider, JsonElement claims, out string[] issuers)
    {
        issuers = [];
        if (!IsMicrosoft(provider))
        {
            issuers = [.. provider.Issuers];
            return issuers.Length > 0;
        }

        if (!TryGetString(claims, "tid", out var tid) || !Guid.TryParseExact(tid, "D", out var tenant) || !TenantAllowed(provider.TenantId, tenant))
        {
            return false;
        }

        issuers = [.. provider.Issuers.Select(issuer => issuer.Replace(ExternalProviderPresets.TenantPlaceholder, tid, StringComparison.Ordinal))];
        return true;
    }

    /// <summary><c>common</c> takes every tenant, <c>organizations</c> every tenant but personal accounts, <c>consumers</c> only them, a GUID only itself.</summary>
    private static bool TenantAllowed(string? configured, Guid tenant) => configured switch
    {
        null or ExternalProviderPresets.DefaultMicrosoftTenant => true,
        OrganizationsTenant => tenant != ConsumersTenant,
        ConsumersTenantName => tenant == ConsumersTenant,
        _ => Guid.TryParseExact(configured, "D", out var only) && tenant == only,
    };

    private static bool IsMicrosoft(ExternalProvider provider) => provider.Name == ExternalProviderPresets.Microsoft;

    /// <summary><c>sub</c>, except Microsoft's <c>{tid}:{oid}</c> (both GUIDs, written lower-case); at most 256 characters, never blank.</summary>
    private static bool TryGetSubject(ExternalProvider provider, JsonElement claims, out string subject)
    {
        subject = string.Empty;
        if (IsMicrosoft(provider))
        {
            if (!TryGetString(claims, "tid", out var tid) || !Guid.TryParseExact(tid, "D", out var tenant)
                || !TryGetString(claims, "oid", out var oid) || !Guid.TryParseExact(oid, "D", out var objectId))
            {
                return false;
            }

            subject = $"{tenant:D}:{objectId:D}";
            return true;
        }

        if (!TryGetString(claims, "sub", out var sub) || string.IsNullOrWhiteSpace(sub) || sub.Length > UserExternalLogin.SubjectMaxLength)
        {
            return false;
        }

        subject = sub;
        return true;
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

    /// <summary>The claim is the boolean <c>true</c> or the string <c>"true"</c> (Apple sends either).</summary>
    private static bool IsTrue(JsonElement claims, string name) =>
        claims.TryGetProperty(name, out var value)
        && (value.ValueKind == JsonValueKind.True
            || (value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

    private static bool NonceMatches(string tokenNonce, string postedNonce) =>
        tokenNonce.Length > 0
        && (string.Equals(tokenNonce, postedNonce, StringComparison.Ordinal)
            || string.Equals(tokenNonce, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(postedNonce))), StringComparison.Ordinal));

    private static bool TryGetIssuedAt(JsonElement claims, out DateTimeOffset issuedAt)
    {
        issuedAt = default;
        if (!claims.TryGetProperty("iat", out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var seconds) || double.IsNaN(seconds) || seconds is < 0 or > MaxUnixSeconds)
        {
            return false;
        }

        issuedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Floor(seconds * 1000));
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
    private static partial void LogDiscoveryFailed(ILogger logger, string provider, string exceptionType);

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
