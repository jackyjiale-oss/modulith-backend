using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TemplateName.Api;
using TemplateName.UnitTests.Auth.DataProtection;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts of the real API in the <c>Production</c> environment, which refuses to start without a signing key and a key ring
/// certificate. Each one gets a database of its own (never the shared one), created empty and migrated the way the pipeline does, a
/// fresh signing key and, unless a test asks for none, a throwaway certificate written to a temporary PFX with a random password and
/// handed over through <c>Auth:DataProtection:Certificate:*</c>.
/// </summary>
internal static class ProductionHosts
{
    private const string CertificateSetting = "Auth:DataProtection:Certificate";

    private static readonly ConcurrentBag<string> TemporaryDirectories = [];

    static ProductionHosts()
    {
        // A host that failed to start never reaches its own clean-up, so the directories go when the test process ends.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var directory in TemporaryDirectories)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException)
                {
                    // Best effort: the directory is in the temporary folder.
                }
            }
        };
    }

    /// <summary>
    /// An empty database named <paramref name="databaseName"/>, migrated, and a Production host on it with a signing key and a
    /// throwaway key ring certificate. The host is built, and started, by the first <c>Services</c> or <c>CreateClient</c> call.
    /// </summary>
    public static async Task<WebApplicationFactory<Program>> CreateAsync(
        IntegrationTestWebAppFactory factory,
        string databaseName,
        CancellationToken cancellationToken)
    {
        var connectionString = await PrepareDatabaseAsync(factory, databaseName, cancellationToken);
        return Create(factory, connectionString, withCertificate: true);
    }

    /// <summary>
    /// Creates the empty database <paramref name="databaseName"/> and migrates it with <see cref="HostCommand.RunMigrationsAsync"/> on a
    /// Testing host, as <c>MigrateCommandTests</c> does. Returns its connection string.
    /// </summary>
    public static async Task<string> PrepareDatabaseAsync(IntegrationTestWebAppFactory factory, string databaseName, CancellationToken cancellationToken)
    {
        var connectionString = await factory.CreateEmptyDatabaseAsync(databaseName, cancellationToken);
        await using var migrating = factory.WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Database", connectionString));
        using var loggerFactory = new LoggerFactory();
        var exitCode = await HostCommand.RunMigrationsAsync(migrating.Services, loggerFactory.CreateLogger("Migrate"), cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Migrating the database '{databaseName}' failed (exit code {exitCode}).");
        }

        return connectionString;
    }

    /// <summary>
    /// A Production host on <paramref name="connectionString"/> with a fresh signing key; with <paramref name="withCertificate"/> it also
    /// gets a throwaway certificate, valid on the factory's clock, which is what the host's startup check reads.
    /// </summary>
    public static WebApplicationFactory<Program> Create(IntegrationTestWebAppFactory factory, string connectionString, bool withCertificate)
    {
        var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = signingKey.ExportPkcs8PrivateKeyPem();
        signingKey.Dispose();

        var settings = new Dictionary<string, string>
        {
            ["ConnectionStrings:Database"] = connectionString,
            ["Auth:Jwt:SigningKeys:0:KeyId"] = "production-test",
            ["Auth:Jwt:SigningKeys:0:PrivateKeyPem"] = pem,
        };

        if (withCertificate)
        {
            // The host reads the factory's fake clock, which starts in January 2026: the certificate is valid around that instant.
            var now = factory.Time.GetUtcNow();
            using var certificate = TestCertificates.Create("production-test", now.AddYears(-1), now.AddYears(10));
            var directory = Path.Combine(Path.GetTempPath(), "production-host-" + Guid.NewGuid().ToString("N"));
            TemporaryDirectories.Add(directory);
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            settings[$"{CertificateSetting}:Source"] = "File";
            settings[$"{CertificateSetting}:Path"] = TestCertificates.WritePfx(certificate, directory, password);
            settings[$"{CertificateSetting}:Password"] = password;
        }

        return factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });
    }

    /// <summary>
    /// A Production host as the <c>migrate</c> mode sees it: built with no key ring certificate and never started. The real mode builds
    /// the host and runs nothing at start (no hosted service, no options validation); a test host runs both when its services are read,
    /// so the hosted services are removed, and a signing key is given because the options validation, which no hosted service does,
    /// is not something a test host can skip.
    /// </summary>
    public static WebApplicationFactory<Program> CreateNeverStarted(IntegrationTestWebAppFactory factory, string connectionString) =>
        Create(factory, connectionString, withCertificate: false).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var hosted = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList();
            foreach (var descriptor in hosted)
            {
                services.Remove(descriptor);
            }
        }));
}
