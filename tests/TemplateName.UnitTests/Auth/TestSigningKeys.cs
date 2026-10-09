using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth;
using TemplateName.Modules.Auth.Infrastructure.Tokens;

namespace TemplateName.UnitTests.Auth;

/// <summary>Builds signing keys, options and key providers for the token tests. Keys are made fresh per call; nothing is read from disk.</summary>
internal static class TestSigningKeys
{
    public const string Issuer = "https://issuer.test";
    public const string Audience = "templatename-api";

    /// <summary>A PKCS#8 PEM private key on <paramref name="curve"/> (P-256 when omitted).</summary>
    public static string PrivateKeyPem(ECCurve? curve = null)
    {
        using var key = ECDsa.Create(curve ?? ECCurve.NamedCurves.nistP256);
        return key.ExportPkcs8PrivateKeyPem();
    }

    /// <summary>The SubjectPublicKeyInfo PEM of a private key PEM.</summary>
    public static string PublicKeyPemOf(string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        return key.ExportSubjectPublicKeyInfoPem();
    }

    public static JwtOptions Options(params JwtOptions.SigningKey[] keys)
    {
        var options = new JwtOptions { Issuer = Issuer, Audience = Audience };
        options.SigningKeys.AddRange(keys);
        return options;
    }

    public static JwtOptions.SigningKey Active(string keyId = "key-2026-10") =>
        new() { KeyId = keyId, PrivateKeyPem = PrivateKeyPem() };

    public static JwtOptions.SigningKey Retired(string keyId, string privateKeyPem) =>
        new() { KeyId = keyId, PublicKeyPem = PublicKeyPemOf(privateKeyPem) };

    public static IHostEnvironment Environment(string name) =>
        new HostingEnvironment { EnvironmentName = name, ApplicationName = "TemplateName.Api", ContentRootPath = AppContext.BaseDirectory };

    public static SigningKeyProvider Provider(JwtOptions options, string environmentName = "Production", ILogger<SigningKeyProvider>? logger = null) =>
        new(Microsoft.Extensions.Options.Options.Create(options), Environment(environmentName), logger ?? NullLogger<SigningKeyProvider>.Instance);

    public static IOptions<JwtOptions> Wrap(JwtOptions options) => Microsoft.Extensions.Options.Options.Create(options);

    /// <summary>The module's real token registrations over in-memory configuration, in the named environment.</summary>
    public static ServiceProvider Services(string environmentName, IDictionary<string, string?>? settings = null, TimeProvider? timeProvider = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build();
        var services = new ServiceCollection();
        services.AddSingleton(Environment(environmentName));
        services.AddSingleton(timeProvider ?? TimeProvider.System);
        services.AddLogging();
        AuthModule.AddAccessTokens(services, configuration);
        return services.BuildServiceProvider();
    }
}
