using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TemplateName.Api;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Infrastructure.DataProtection;
using TemplateName.UnitTests.Auth.DataProtection;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// The Data Protection key ring in Production: wrapped with the configured certificate, and the host refusing to start with a plaintext
/// ring or no certificate. Each test uses a database of its own, so the shared test database and its key ring are not touched.
/// </summary>
public sealed class KeyRingProtectionTests(IntegrationTestWebAppFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Production_starts_with_a_certificate_and_writes_a_protected_ring()
    {
        await using var production = await ProductionHosts.CreateAsync(factory, "TemplateName_KeyRingProtected", Ct);
        using var client = production.CreateClient();

        // The first use of a protector makes the ring's first key.
        production.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("key-ring-test").Protect("secret");

        var documents = await ReadRingAsync(ConnectionStringOf(production), Ct);
        documents.ShouldNotBeEmpty();
        documents.ShouldAllBe(document => document.Contains("encryptedSecret", StringComparison.Ordinal));
        documents.ShouldNotContain(document => document.Contains("masterKey", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Production_does_not_start_without_a_certificate()
    {
        var connectionString = await ProductionHosts.PrepareDatabaseAsync(factory, "TemplateName_KeyRingNoCertificate", Ct);
        await using var production = ProductionHosts.Create(factory, connectionString, withCertificate: false);

        var failure = Should.Throw<InvalidOperationException>(() => production.CreateClient());

        failure.Message.ShouldContain("Auth:DataProtection:Certificate");
        failure.Message.ShouldContain("docs/runbooks/restore-backup.md");
    }

    [Fact]
    public async Task Production_refuses_a_plaintext_ring()
    {
        var connectionString = await ProductionHosts.PrepareDatabaseAsync(factory, "TemplateName_KeyRingPlaintext", Ct);
        var plaintext = KeyRingFixtures.CreateRing(certificate: null).Single();
        await InsertKeyAsync(connectionString, plaintext, Ct);
        await using var production = ProductionHosts.Create(factory, connectionString, withCertificate: true);

        var failure = Should.Throw<InvalidOperationException>(() => production.CreateClient());

        failure.Message.ShouldContain("plaintext");
    }

    [Fact]
    public async Task Migrate_runs_in_production_with_no_certificate()
    {
        var connectionString = await factory.CreateEmptyDatabaseAsync("TemplateName_KeyRingMigrate", Ct);
        await using var production = ProductionHosts.CreateNeverStarted(factory, connectionString);
        using var loggerFactory = new LoggerFactory();

        var exitCode = await HostCommand.RunMigrationsAsync(production.Services, loggerFactory.CreateLogger("Migrate"), Ct);

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task The_ring_reader_treats_a_missing_table_as_an_empty_ring()
    {
        var connectionString = await factory.CreateEmptyDatabaseAsync("TemplateName_KeyRingUnmigrated", Ct);
        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Database", connectionString));
        var reader = new DbKeyRingReader(host.Services.GetRequiredService<IServiceScopeFactory>());

        var documents = await reader.ReadDocumentsAsync(Ct);

        documents.ShouldBeEmpty();
    }

    private static string ConnectionStringOf(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<IConfiguration>().GetConnectionString("Database")
        ?? throw new InvalidOperationException("The host has no database connection string.");

    private static async Task<List<string>> ReadRingAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Xml FROM auth.DataProtectionKeys";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var documents = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            documents.Add(reader.GetString(0));
        }

        return documents;
    }

    private static async Task InsertKeyAsync(string connectionString, string xml, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO auth.DataProtectionKeys (FriendlyName, Xml) VALUES (@name, @xml)";
        command.Parameters.AddWithValue("@name", "key-plaintext");
        command.Parameters.AddWithValue("@xml", xml);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
