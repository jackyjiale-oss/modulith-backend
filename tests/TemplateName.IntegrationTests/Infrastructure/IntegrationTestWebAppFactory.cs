using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Respawn;
using Respawn.Graph;
using TemplateName.Application.Common.Identity;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.IntegrationTests.Persistence;
using Testcontainers.MsSql;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the real API in the <c>Testing</c> environment against a SQL Server container. One instance is shared by the whole assembly.
/// </summary>
public sealed class IntegrationTestWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string TestsDatabaseName = "TemplateName_Tests";
    private const string PersistenceTestsDatabaseName = "TemplateName_PersistenceTests";
    private const string PersistenceTestsConnectionStringName = "PersistenceTests";
    private const string CreateDatabasesSql = $"CREATE DATABASE [{TestsDatabaseName}]; CREATE DATABASE [{PersistenceTestsDatabaseName}];";

    private static readonly DateTimeOffset StartTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly MsSqlContainer _sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private ResettableDatabase? _testsDatabase;
    private ResettableDatabase? _persistenceTestsDatabase;

    /// <summary>The clock every host built by this factory uses; it starts at 2026-01-01T00:00:00Z and moves only when a test moves it.</summary>
    public FakeTimeProvider Time { get; } = new(StartTime);

    /// <summary>The user every host built by this factory sees; anonymous until a test sets <see cref="TestCurrentUser.UserId"/>.</summary>
    public TestCurrentUser CurrentUser { get; } = new();

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _sqlServer.StartAsync(cancellationToken);

        await using (var master = new SqlConnection(_sqlServer.GetConnectionString()))
        {
            await master.OpenAsync(cancellationToken);
            await using var command = master.CreateCommand();
            command.CommandText = CreateDatabasesSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        _testsDatabase = new ResettableDatabase(ConnectionStringFor(TestsDatabaseName));
        _persistenceTestsDatabase = new ResettableDatabase(ConnectionStringFor(PersistenceTestsDatabaseName));

        // Reading Services builds and starts the host.
        await Services.MigrateModuleDatabasesAsync(cancellationToken);
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TestDbContext>().Database.EnsureCreatedAsync(cancellationToken);
    }

    /// <summary>Deletes the rows of every table (except migration history) in both test databases.</summary>
    public async Task ResetDatabasesAsync(CancellationToken cancellationToken)
    {
        await TestsDatabase.ResetAsync(cancellationToken);
        await PersistenceTestsDatabase.ResetAsync(cancellationToken);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _sqlServer.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Host settings, not an in-memory source, so tests can override them with UseSetting on a derived factory.
        builder.UseSetting("RateLimiting:GlobalPermitLimit", "100000");
        builder.UseSetting("ConnectionStrings:Database", TestsDatabase.ConnectionString);
        builder.UseSetting($"ConnectionStrings:{PersistenceTestsConnectionStringName}", PersistenceTestsDatabase.ConnectionString);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Outbox:Enabled"] = "false",

                // Keep test output to problems: no per-request or host-lifetime lines.
                ["Serilog:MinimumLevel:Default"] = "Warning",
            }));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            services.RemoveAll<ICurrentUser>();
            services.AddSingleton<ICurrentUser>(CurrentUser);
            services.AddModuleDbContext<TestDbContext>(TestDbContext.Schema, PersistenceTestsConnectionStringName, includeInMigrations: false);
        });
    }

    private ResettableDatabase TestsDatabase
        => _testsDatabase ?? throw new InvalidOperationException("The SQL Server container has not been started.");

    private ResettableDatabase PersistenceTestsDatabase
        => _persistenceTestsDatabase ?? throw new InvalidOperationException("The SQL Server container has not been started.");

    private string ConnectionStringFor(string databaseName)
        => new SqlConnectionStringBuilder(_sqlServer.GetConnectionString()) { InitialCatalog = databaseName }.ConnectionString;

    /// <summary>One database and its Respawner, created on the first reset that finds tables to clear.</summary>
    private sealed class ResettableDatabase(string connectionString)
    {
        private const string HasTablesSql =
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME <> '__EFMigrationsHistory') THEN 1 ELSE 0 END";

        private static readonly RespawnerOptions RespawnerOptions = new()
        {
            DbAdapter = DbAdapter.SqlServer,
            TablesToIgnore = [new Table("__EFMigrationsHistory")],
        };

        private Respawner? _respawner;

        public string ConnectionString { get; } = connectionString;

        public async Task ResetAsync(CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);

            if (_respawner is null)
            {
                if (!await HasTablesAsync(connection, cancellationToken))
                {
                    return;
                }

                _respawner = await Respawner.CreateAsync(connection, RespawnerOptions);
            }

            await _respawner.ResetAsync(connection);
        }

        private static async Task<bool> HasTablesAsync(SqlConnection connection, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = HasTablesSql;
            return (int)(await command.ExecuteScalarAsync(cancellationToken))! == 1;
        }
    }
}
