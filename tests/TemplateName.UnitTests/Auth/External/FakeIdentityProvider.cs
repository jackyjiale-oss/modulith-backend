using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TemplateName.Modules.Auth;
using TemplateName.Modules.Auth.Application.Abstractions;

namespace TemplateName.UnitTests.Auth.External;

/// <summary>
/// An OpenID Connect provider for tests, with no network: an RSA key (and an EC P-256 key) that sign ID tokens, and a <see cref="Handler"/>
/// that answers any host's <c>/.well-known/openid-configuration</c> and the key set it points to. Put the handler under the
/// <c>oidc-discovery</c> client and the verifier reaches it at the real provider's address. Shared with the integration project.
/// </summary>
internal sealed class FakeIdentityProvider : IDisposable
{
    public const string GoogleIssuer = "https://accounts.google.com";
    public const string DefaultClientId = "fake-client.apps.example";
    public const string DefaultSubject = "fake-subject-0001";
    public const string DefaultEmail = "alice@example.com";
    public const string DefaultName = "Alice Example";

    /// <summary>
    /// The <c>hd</c> claim of every token unless a customization removes it: the default identity is a Google Workspace account of
    /// <see cref="DefaultEmail"/>'s domain, so its verified email is usable under Google's rule (Gmail or <c>hd</c>, Ruling R15). Other
    /// providers ignore the claim.
    /// </summary>
    public const string DefaultHostedDomain = "example.com";

    /// <summary>The <c>AppSecret</c> <see cref="ToProvider"/> gives a Facebook provider.</summary>
    public const string DefaultFacebookAppSecret = "fake-facebook-app-secret";

    /// <summary>The raw nonce <see cref="IdToken"/> binds unless told otherwise (sixteen characters or more, as the API requires).</summary>
    public const string DefaultNonce = "nonce-0123456789abcdef";

    /// <summary>The tenant of Microsoft's personal accounts.</summary>
    public const string MicrosoftConsumersTenant = "9188040d-6c67-4c5b-b112-36a304b66dad";

    private const string JwksPath = "/fake-jwks";
    private const string DiscoverySuffix = "/.well-known/openid-configuration";
    private const string DiscoverySlashSuffix = DiscoverySuffix + "/";

    private readonly List<(string KeyId, RSA Key)> _rsaKeys = [];
    private readonly ECDsa _ecKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly RSA _strangerKey = RSA.Create(2048);
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public FakeIdentityProvider(TimeProvider? time = null, string issuer = GoogleIssuer)
    {
        Time = time ?? TimeProvider.System;
        Issuer = issuer;
        _rsaKeys.Add(("fake-rsa-1", RSA.Create(2048)));
        Handler = new FakeIdentityProviderHandler(this);
    }

    /// <summary>The clock of <c>iat</c>, <c>nbf</c> and <c>exp</c>.</summary>
    public TimeProvider Time { get; }

    /// <summary>The <c>iss</c> of every token unless a customization changes it.</summary>
    public string Issuer { get; }

    /// <summary>The key that signs RS256 tokens now (the newest after <see cref="RotateKey"/>).</summary>
    public RSA Key => _rsaKeys[^1].Key;

    /// <summary>The <c>kid</c> of <see cref="Key"/>.</summary>
    public string KeyId => _rsaKeys[^1].KeyId;

    /// <summary>The <c>kid</c> of the published EC key (<see cref="Signing.Es256"/>).</summary>
    public string EcKeyId => "fake-ec-1";

    /// <summary>Serves discovery and the key set; it can be told to fail or hang.</summary>
    public FakeIdentityProviderHandler Handler { get; }

    /// <summary>Microsoft's issuer for the tenant <paramref name="tenantId"/>.</summary>
    public static string MicrosoftIssuer(string tenantId) => $"https://login.microsoftonline.com/{tenantId}/v2.0";

    /// <summary>
    /// The provider <paramref name="name"/> as the catalog builds it from configuration, enabled with <paramref name="clientId"/>: the preset
    /// of Google, Apple, Microsoft or Facebook, or a generic OIDC entry whose authority (and issuer) is <see cref="Issuer"/>.
    /// </summary>
    public ExternalProvider ToProvider(string name = "google", string? clientId = null, string? tenantId = null, bool? trustEmail = null)
    {
        var prefix = $"Auth:External:Providers:{name}";
        var settings = new Dictionary<string, string?> { [$"{prefix}:Enabled"] = "true" };
        if (string.Equals(name, "facebook", StringComparison.OrdinalIgnoreCase))
        {
            settings[$"{prefix}:AppId"] = clientId ?? DefaultClientId;
            settings[$"{prefix}:AppSecret"] = DefaultFacebookAppSecret;
        }
        else
        {
            settings[$"{prefix}:ClientIds:0"] = clientId ?? DefaultClientId;
        }

        if (name.ToLowerInvariant() is not ("google" or "apple" or "microsoft" or "facebook"))
        {
            settings[$"{prefix}:Type"] = "oidc";
            settings[$"{prefix}:Authority"] = Issuer;
        }

        if (tenantId is not null)
        {
            settings[$"{prefix}:TenantId"] = tenantId;
        }

        if (trustEmail is not null)
        {
            settings[$"{prefix}:TrustEmail"] = trustEmail.Value ? "true" : "false";
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        AuthModule.AddExternalLogin(services, configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IExternalProviderCatalog>().Find(name)
            ?? throw new InvalidOperationException($"The fake provider '{name}' did not build.");
    }

    /// <summary>
    /// A signed ID token: <c>iss</c> <see cref="Issuer"/>, <c>aud</c> <see cref="DefaultClientId"/>, <c>sub</c>, <c>iat</c> and <c>nbf</c> now,
    /// <c>exp</c> in an hour, <c>nonce</c> <see cref="DefaultNonce"/>, a verified <c>email</c>, <c>hd</c> <see cref="DefaultHostedDomain"/> and a <c>name</c>. <paramref name="customize"/>
    /// changes or removes claims; <paramref name="signWith"/> picks a <see cref="Signing"/> variant (RS256 with <see cref="Key"/> when null).
    /// </summary>
    public string IdToken(Action<IDictionary<string, object>>? customize = null, string? signWith = null)
    {
        var now = Time.GetUtcNow().ToUnixTimeSeconds();
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["iss"] = Issuer,
            ["aud"] = DefaultClientId,
            ["sub"] = DefaultSubject,
            ["iat"] = now,
            ["nbf"] = now,
            ["exp"] = now + 3600,
            ["nonce"] = DefaultNonce,
            ["email"] = DefaultEmail,
            ["email_verified"] = true,
            ["hd"] = DefaultHostedDomain,
            ["name"] = DefaultName,
        };
        customize?.Invoke(claims);

        return _handler.CreateToken(new SecurityTokenDescriptor { Claims = claims, SigningCredentials = Credentials(signWith) });
    }

    /// <summary>Starts signing with a new RSA key under a new <c>kid</c>; the key set publishes it next to the old one.</summary>
    public void RotateKey()
    {
        lock (_rsaKeys)
        {
            _rsaKeys.Add(($"fake-rsa-{_rsaKeys.Count + 1}", RSA.Create(2048)));
        }
    }

    /// <summary>The lower-case hex SHA-256 of a nonce, as Apple's native SDK puts it in the token.</summary>
    public static string HashNonce(string nonce) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(nonce)));

    public void Dispose()
    {
        foreach (var (_, key) in _rsaKeys)
        {
            key.Dispose();
        }

        _ecKey.Dispose();
        _strangerKey.Dispose();
        Handler.Dispose();
    }

    private SigningCredentials? Credentials(string? signWith) => signWith switch
    {
        null => new SigningCredentials(new RsaSecurityKey(Key) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256),
        Signing.None => null,
        Signing.Hs256WithPublicKey => new SigningCredentials(
            new SymmetricSecurityKey(Key.ExportSubjectPublicKeyInfo()) { KeyId = KeyId },
            SecurityAlgorithms.HmacSha256),
        Signing.Es256 => new SigningCredentials(new ECDsaSecurityKey(_ecKey) { KeyId = EcKeyId }, SecurityAlgorithms.EcdsaSha256),
        Signing.UnknownKey => new SigningCredentials(new RsaSecurityKey(_strangerKey) { KeyId = "fake-rsa-unknown" }, SecurityAlgorithms.RsaSha256),
        Signing.StrangerUnderKnownKid => new SigningCredentials(new RsaSecurityKey(_strangerKey) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256),
        Signing.NoKid => new SigningCredentials(new RsaSecurityKey(Key), SecurityAlgorithms.RsaSha256),
        Signing.Rs384 => new SigningCredentials(new RsaSecurityKey(Key) { KeyId = KeyId }, SecurityAlgorithms.RsaSha384),
        _ => throw new ArgumentOutOfRangeException(nameof(signWith), signWith, "Unknown signing variant."),
    };

    private string DiscoveryDocument(Uri request, bool includeKeySetAddress)
    {
        var suffixLength = request.AbsolutePath.EndsWith(DiscoverySlashSuffix, StringComparison.Ordinal)
            ? DiscoverySlashSuffix.Length
            : DiscoverySuffix.Length;
        var path = request.AbsolutePath[..^suffixLength];
        var document = new Dictionary<string, object>
        {
            ["issuer"] = Issuer,
            ["authorization_endpoint"] = $"{request.Scheme}://{request.Authority}{path}/authorize",
            ["response_types_supported"] = new[] { "id_token" },
            ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { "RS256", "ES256" },
        };
        if (includeKeySetAddress)
        {
            document["jwks_uri"] = $"{request.Scheme}://{request.Authority}{path}{JwksPath}";
        }

        return JsonSerializer.Serialize(document);
    }

    private string KeySet()
    {
        var keys = new List<Dictionary<string, string>>();
        lock (_rsaKeys)
        {
            foreach (var (keyId, key) in _rsaKeys)
            {
                var parameters = key.ExportParameters(includePrivateParameters: false);
                keys.Add(new Dictionary<string, string>
                {
                    ["kty"] = "RSA",
                    ["use"] = "sig",
                    ["alg"] = "RS256",
                    ["kid"] = keyId,
                    ["n"] = Base64UrlEncoder.Encode(parameters.Modulus),
                    ["e"] = Base64UrlEncoder.Encode(parameters.Exponent),
                });
            }
        }

        var ec = _ecKey.ExportParameters(includePrivateParameters: false);
        keys.Add(new Dictionary<string, string>
        {
            ["kty"] = "EC",
            ["use"] = "sig",
            ["alg"] = "ES256",
            ["crv"] = "P-256",
            ["kid"] = EcKeyId,
            ["x"] = Base64UrlEncoder.Encode(ec.Q.X),
            ["y"] = Base64UrlEncoder.Encode(ec.Q.Y),
        });

        return JsonSerializer.Serialize(new { keys });
    }

    /// <summary>The ways <see cref="IdToken"/> can sign, for the forgery cases.</summary>
    public static class Signing
    {
        /// <summary>No signature: <c>alg: none</c>.</summary>
        public const string None = "none";

        /// <summary>HS256 keyed with the published RSA public key's bytes, under the real <c>kid</c> (algorithm confusion).</summary>
        public const string Hs256WithPublicKey = "HS256-public-key";

        /// <summary>ES256 with the published EC key.</summary>
        public const string Es256 = "ES256";

        /// <summary>RS256 with a key the provider never published, under a <c>kid</c> it does not know.</summary>
        public const string UnknownKey = "unknown-key";

        /// <summary>RS256 with a key the provider never published, labelled with the real <c>kid</c>.</summary>
        public const string StrangerUnderKnownKid = "stranger-under-known-kid";

        /// <summary>RS256 with the real key but no <c>kid</c> in the header.</summary>
        public const string NoKid = "no-kid";

        /// <summary>RS384 with the real key and <c>kid</c>: a valid signature with an algorithm outside the allow-list.</summary>
        public const string Rs384 = "RS384";
    }

    /// <summary>The fake's HTTP side. Counts discovery and key-set requests; <see cref="FailWith"/> and <see cref="Hang"/> break it.</summary>
    internal sealed class FakeIdentityProviderHandler(FakeIdentityProvider provider) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Uri> _requests = new();
        private int _discoveryRequests;
        private int _keySetRequests;
        private volatile Mode _mode = Mode.Serve;
        private volatile bool _slashedDiscovery;
        private HttpStatusCode _failure = HttpStatusCode.InternalServerError;

        private enum Mode
        {
            Serve,
            Fail,
            FailKeySet,
            Hang,
            NoKeySetAddress,
            EmptyKeySet,
        }

        /// <summary>Every address requested, in order.</summary>
        public IReadOnlyCollection<Uri> Requests => _requests;

        public int DiscoveryRequests => Volatile.Read(ref _discoveryRequests);

        public int KeySetRequests => Volatile.Read(ref _keySetRequests);

        /// <summary>Answers every request with <paramref name="status"/> until <see cref="Recover"/>.</summary>
        public void FailWith(HttpStatusCode status)
        {
            _failure = status;
            _mode = Mode.Fail;
        }

        /// <summary>Serves discovery but answers the key set with <paramref name="status"/> until <see cref="Recover"/>.</summary>
        public void FailKeySetWith(HttpStatusCode status)
        {
            _failure = status;
            _mode = Mode.FailKeySet;
        }

        /// <summary>Never answers until <see cref="Recover"/>; a request ends only when it is cancelled.</summary>
        public void Hang() => _mode = Mode.Hang;

        /// <summary>Serves a discovery document without <c>jwks_uri</c> until <see cref="Recover"/>.</summary>
        public void OmitKeySetAddress() => _mode = Mode.NoKeySetAddress;

        /// <summary>Serves a key set with no key until <see cref="Recover"/>.</summary>
        public void ServeEmptyKeySet() => _mode = Mode.EmptyKeySet;

        public void Recover() => _mode = Mode.Serve;

        /// <summary>
        /// Answers discovery only at <c>/.well-known/openid-configuration/</c>, with the trailing slash (as Facebook's address has it), and
        /// answers the form without the slash with 404. By default it is the reverse: only the form without the slash is answered, so a
        /// verifier that adds or drops a slash is caught either way.
        /// </summary>
        public void ServeDiscoveryWithTrailingSlashOnly() => _slashedDiscovery = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("A request without an address.");
            _requests.Enqueue(uri);
            var isSlashedDiscovery = uri.AbsolutePath.EndsWith(DiscoverySlashSuffix, StringComparison.Ordinal);
            var isDiscovery = isSlashedDiscovery || uri.AbsolutePath.EndsWith(DiscoverySuffix, StringComparison.Ordinal);
            var isKeySet = uri.AbsolutePath.EndsWith(JwksPath, StringComparison.Ordinal);
            if (isDiscovery)
            {
                Interlocked.Increment(ref _discoveryRequests);
                if (isSlashedDiscovery != _slashedDiscovery)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
            }
            else if (isKeySet)
            {
                Interlocked.Increment(ref _keySetRequests);
            }

            switch (_mode)
            {
                case Mode.Fail:
                case Mode.FailKeySet when isKeySet:
                    return new HttpResponseMessage(_failure);
                case Mode.Hang:
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    return new HttpResponseMessage(HttpStatusCode.OK);
            }

            var mode = _mode;
            if (isDiscovery)
            {
                return Json(provider.DiscoveryDocument(uri, includeKeySetAddress: mode != Mode.NoKeySetAddress));
            }

            if (isKeySet)
            {
                return Json(mode == Mode.EmptyKeySet ? """{"keys":[]}""" : provider.KeySet());
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
