using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
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
using TemplateName.Application.Common.Messaging;
using TemplateName.Infrastructure.Common.Inbox;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.IntegrationTests.Outbox;
using TemplateName.IntegrationTests.Persistence;
using TemplateName.Modules.Auth;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Infrastructure.Persistence;
using TemplateName.SharedKernel;
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
    private const int TestPasswordHashIterations = 1000;

    private static readonly DateTimeOffset StartTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly MsSqlContainer _sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private ResettableDatabase? _testsDatabase;
    private ResettableDatabase? _persistenceTestsDatabase;

    /// <summary>The clock every host built by this factory uses; it starts at 2026-01-01T00:00:00Z and moves only when a test moves it.</summary>
    public FakeTimeProvider Time { get; } = new(StartTime);

    /// <summary>
    /// The user every host built by this factory sees: the user a test forces with <see cref="TestCurrentUser.UserId"/>, else the caller
    /// the request's access token names, else anonymous.
    /// </summary>
    public TestCurrentUser CurrentUser { get; } = new(new HttpContextAccessor());

    /// <summary>A permission source tests change to declare or drop permissions before seeding again; empty at the start of every test.</summary>
    public TestPermissionSource PermissionSource { get; } = new();

    /// <summary>Every email the hosts built by this factory sent, kept in memory instead of going to SMTP; empty at the start of every test.</summary>
    internal RecordingEmailSender EmailSender { get; } = new();

    /// <summary>The events the outbox test handlers have received.</summary>
    public EventRecorder EventRecorder { get; } = new();

    /// <summary>
    /// The Auth integration events the hosts built by this factory published, recorded by
    /// <see cref="RecordingIntegrationEventHandler{TEvent}"/> in place of the consuming modules; empty at the start of every test.
    /// </summary>
    public IntegrationEventRecorder IntegrationEvents { get; } = new();

    /// <summary>The outbox message contexts the message context test handler has read.</summary>
    public MessageContextRecorder MessageContextRecorder { get; } = new();

    /// <summary>Makes the flaky outbox test handlers fail.</summary>
    public FlakySwitch FlakySwitch { get; } = new();

    /// <summary>Parks the gate outbox test handler inside a message.</summary>
    public HandlerGate HandlerGate { get; } = new();

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

        // Reading Services builds and starts the host. The host does not seed on start (Auth:Seed:RunOnStartup is off), so seed here.
        await Services.MigrateModuleDatabasesAsync(cancellationToken);
        await Services.SeedAuthModuleAsync(cancellationToken);
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TestDbContext>().Database.EnsureCreatedAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes the rows of every table in both test databases except the migration history and the Data Protection key ring
    /// (<c>auth.DataProtectionKeys</c>): the hosts keep their key rings in memory across tests, as production never deletes keys, so a
    /// host whose ring is reloaded from the database still finds every key it has protected with.
    /// </summary>
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
        builder.UseSetting("RateLimiting:AuthStrictPermitLimit", "100000");
        builder.UseSetting("Auth:Seed:RunOnStartup", "false");

        // No calls to the real Have I Been Pwned service from tests; HibpBreachedPasswordCheckerTests cover the checker.
        builder.UseSetting("Auth:Password:CheckBreached", "false");
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
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(EmailSender);

            // Production keeps the Identity default iteration count and has no setting for it; tests hash far more often.
            services.Configure<PasswordHasherOptions>(options => options.IterationCount = TestPasswordHashIterations);
            services.AddModuleDbContext<TestDbContext>(TestDbContext.Schema, PersistenceTestsConnectionStringName, includeInMigrations: false);

            // A recording consumer for each Auth integration event, before the scanned test handlers, so it runs first.
            AddRecordingIntegrationEventHandler<EmailVerificationRequestedIntegrationEvent>(services);
            AddRecordingIntegrationEventHandler<PasswordResetRequestedIntegrationEvent>(services);
            AddRecordingIntegrationEventHandler<RegistrationAttemptedIntegrationEvent>(services);
            AddRecordingIntegrationEventHandler<PasswordChangedIntegrationEvent>(services);
            AddRecordingIntegrationEventHandler<UserLockedOutIntegrationEvent>(services);
            AddRecordingIntegrationEventHandler<RefreshTokenReuseDetectedIntegrationEvent>(services);
            services.AddSingleton(IntegrationEvents);
            services.AddApplicationHandlers(typeof(IntegrationTestWebAppFactory).Assembly);
            services.AddOutbox<TestDbContext>(typeof(IntegrationTestWebAppFactory).Assembly);
            services.AddInbox<TestDbContext>();
            services.AddSingleton<IPermissionSource>(PermissionSource);
            services.AddSingleton(EventRecorder);
            services.AddSingleton(MessageContextRecorder);
            services.AddSingleton(FlakySwitch);
            services.AddSingleton(HandlerGate);
            services.AddSingleton<IStartupFilter, ProtectedTestEndpointStartupFilter>();
            services.AddSingleton<IStartupFilter, TestClientAddressStartupFilter>();
        });
    }

    /// <summary>Registers the recording consumer of <typeparamref name="TEvent"/> as <c>AddApplicationHandlers</c> registers a scanned one.</summary>
    private static void AddRecordingIntegrationEventHandler<TEvent>(IServiceCollection services)
        where TEvent : IIntegrationEvent
        => services.AddIntegrationEventHandler(typeof(TEvent), typeof(RecordingIntegrationEventHandler<TEvent>));

    private ResettableDatabase TestsDatabase
        => _testsDatabase ?? throw new InvalidOperationException("The SQL Server container has not been started.");

    private ResettableDatabase PersistenceTestsDatabase
        => _persistenceTestsDatabase ?? throw new InvalidOperationException("The SQL Server container has not been started.");

    private string ConnectionStringFor(string databaseName)
        => new SqlConnectionStringBuilder(_sqlServer.GetConnectionString()) { InitialCatalog = databaseName }.ConnectionString;

    /// <summary>One database and its Respawner, created on the first reset that finds tables to clear.</summary>
    private sealed class ResettableDatabase(string connectionString)
    {
        private const string DataProtectionKeysTable = "DataProtectionKeys";

        private const string HasTablesSql =
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME <> '__EFMigrationsHistory') THEN 1 ELSE 0 END";

        // The key ring stays: a host that protected a value with a key it cached must find that key again when Data Protection reloads
        // its ring from the database (after a new key invalidates the cache, or on an unknown key id). Deleting the rows made that
        // reload create a fresh key and drop the cached one, so a token protected before it could no longer be unprotected.
        private static readonly RespawnerOptions RespawnerOptions = new()
        {
            DbAdapter = DbAdapter.SqlServer,
            TablesToIgnore = [new Table("__EFMigrationsHistory"), new Table(AuthDbContext.Schema, DataProtectionKeysTable)],
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
