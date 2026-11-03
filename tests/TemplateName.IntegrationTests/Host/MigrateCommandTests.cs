using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using TemplateName.Api;
using TemplateName.IntegrationTests.Infrastructure;

namespace TemplateName.IntegrationTests.Host;

/// <summary>The API's <c>migrate</c> mode: <see cref="HostCommand.RunMigrationsAsync"/> against a database of its own, never the shared one.</summary>
public sealed class MigrateCommandTests(IntegrationTestWebAppFactory factory)
{
    private const string UnreachablePassword = "Pa55-secret-value";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrate_creates_every_module_schema_and_is_idempotent()
    {
        var connectionString = await factory.CreateEmptyDatabaseAsync("TemplateName_MigrateCommand", Ct);
        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Database", connectionString));
        using var logs = new RecordingLoggerProvider();
        using var loggerFactory = new LoggerFactory([logs]);
        var logger = loggerFactory.CreateLogger("Migrate");

        var first = await HostCommand.RunMigrationsAsync(host.Services, logger, Ct);

        first.ShouldBe(0);
        var schemas = await QueryAsync<string>(connectionString, "SELECT name FROM sys.schemas", Ct);
        schemas.ShouldContain("platform");
        schemas.ShouldContain("auth");
        schemas.ShouldContain("notify");
        schemas.ShouldContain("sample");
        schemas.ShouldContain("audit");
        var countsAfterFirst = await HistoryRowCountsAsync(connectionString, Ct);
        countsAfterFirst.ShouldNotBeEmpty();

        var second = await HostCommand.RunMigrationsAsync(host.Services, logger, Ct);

        second.ShouldBe(0);
        (await HistoryRowCountsAsync(connectionString, Ct)).ShouldBe(countsAfterFirst);
    }

    [Fact]
    public async Task Migrate_exits_1_for_an_unreachable_server_in_bounded_time_without_logging_the_password()
    {
        var connectionString = $"Server=127.0.0.1,1;Database=x;User Id=sa;Password={UnreachablePassword};Connect Timeout=2;TrustServerCertificate=true";
        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Database", connectionString));
        using var logs = new RecordingLoggerProvider();
        using var loggerFactory = new LoggerFactory([logs]);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));

        var exitCode = await HostCommand.RunMigrationsAsync(host.Services, loggerFactory.CreateLogger("Migrate"), timeout.Token);

        exitCode.ShouldBe(1);
        timeout.IsCancellationRequested.ShouldBeFalse("the command must give up by itself, not because the test cancelled it");
        logs.Entries.ShouldContain(entry => entry.StartsWith("Error "));
        logs.Entries.ShouldNotContain(entry => entry.Contains(UnreachablePassword));
    }

    private static async Task<List<T>> QueryAsync<T>(string connectionString, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(reader.GetFieldValue<T>(0));
        }

        return rows;
    }

    /// <summary>The row count of every <c>__EFMigrationsHistory</c> table, keyed by its schema.</summary>
    private static async Task<Dictionary<string, int>> HistoryRowCountsAsync(string connectionString, CancellationToken cancellationToken)
    {
        var schemas = await QueryAsync<string>(
            connectionString,
            "SELECT s.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE t.name = '__EFMigrationsHistory'",
            cancellationToken);
        var counts = new Dictionary<string, int>();
        foreach (var schema in schemas)
        {
            var count = await QueryAsync<int>(connectionString, $"SELECT COUNT(*) FROM [{schema}].[__EFMigrationsHistory]", cancellationToken);
            counts[schema] = count.Single();
        }

        return counts;
    }
}
