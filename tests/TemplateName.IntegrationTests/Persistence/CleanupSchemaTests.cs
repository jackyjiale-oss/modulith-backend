using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Persistence;

/// <summary>
/// The schema the cleanup jobs rely on: every predicate a job deletes by has an index whose first key column is the column the
/// predicate tests, and the scheduler's <c>platform.JobStates</c> table has the columns the design lists. All module and platform
/// tables live in the one test database, so one connection reads them all.
/// </summary>
public sealed class CleanupSchemaTests(IntegrationTestWebAppFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string, bool> CleanupPredicates => new()
    {
        { "platform.IdempotencyKeys", "ExpiresAt", false },
        { "auth.OutboxMessages", "ProcessedAt", true },
        { "sample.OutboxMessages", "ProcessedAt", true },
        { "notify.InboxMessages", "ProcessedAt", false },
        { "auth.VerificationCodes", "ExpiresAt", false },
        { "auth.RefreshTokens", "ExpiresAt", false },
        { "auth.UserSessions", "ExpiresAt", false },
        { "auth.UserSessions", "RevokedAt", true },
        { "auth.MfaChallenges", "ExpiresAt", false },
        { "auth.TrustedDevices", "ExpiresAt", false },
        { "auth.TrustedDevices", "RevokedAt", true },
        { "auth.AuthAuditLogs", "OccurredAt", false },
        { "notify.HubTickets", "ExpiresAt", false },
        { "notify.Notifications", "CreatedAt", false },
        { "notify.InAppNotifications", "CreatedAt", false },
    };

    [Theory]
    [MemberData(nameof(CleanupPredicates))]
    public async Task Every_cleanup_predicate_has_its_index(string table, string leadingColumn, bool filtered)
    {
        const string Sql = """
            SELECT COUNT(*)
            FROM sys.indexes AS i
            INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1
            INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@table) AND c.name = @column AND (@filtered = 0 OR i.has_filter = 1)
            """;

        await using var scope = factory.Services.CreateAsyncScope();
        var connection = scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.GetDbConnection();
        await connection.OpenAsync(Ct);

        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        AddParameter(command, "@table", table);
        AddParameter(command, "@column", leadingColumn);
        AddParameter(command, "@filtered", filtered ? 1 : 0);

        var indexes = (int)(await command.ExecuteScalarAsync(Ct))!;

        indexes.ShouldBeGreaterThan(0, $"{table} has no {(filtered ? "filtered " : string.Empty)}index that leads with {leadingColumn}");
    }

    [Fact]
    public async Task JobStates_has_the_columns_of_the_spec()
    {
        const string ColumnsSql = """
            SELECT c.name, t.name, c.max_length, c.is_nullable, c.precision, c.scale
            FROM sys.columns AS c
            INNER JOIN sys.types AS t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID('platform.JobStates')
            """;
        const string PrimaryKeySql = """
            SELECT c.name
            FROM sys.indexes AS i
            INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('platform.JobStates') AND i.is_primary_key = 1
            """;

        await using var scope = factory.Services.CreateAsyncScope();
        var connection = scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.GetDbConnection();
        await connection.OpenAsync(Ct);

        var columns = new Dictionary<string, (string Type, int MaxLength, bool Nullable, int Scale)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = ColumnsSql;
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                columns[reader.GetString(0)] = (reader.GetString(1), Convert.ToInt32(reader.GetValue(2)), reader.GetBoolean(3), Convert.ToInt32(reader.GetValue(5)));
            }
        }

        columns.Keys.ShouldBe(
            ["Name", "NextRunAt", "LockedUntil", "LastStartedAt", "LastFinishedAt", "LastStatus", "LastError", "LastProcessed", "RowVersion"],
            ignoreOrder: true);

        columns["Name"].ShouldBe(("nvarchar", 400, false, 0));
        columns["NextRunAt"].ShouldBe(("datetime2", 7, false, 3));
        columns["LockedUntil"].ShouldBe(("datetime2", 7, true, 3));
        columns["LastStartedAt"].ShouldBe(("datetime2", 7, true, 3));
        columns["LastFinishedAt"].ShouldBe(("datetime2", 7, true, 3));
        columns["LastStatus"].Type.ShouldBe("nvarchar");
        columns["LastStatus"].Nullable.ShouldBeTrue();
        columns["LastError"].ShouldBe(("nvarchar", 4000, true, 0));
        columns["LastProcessed"].ShouldBe(("int", 4, true, 0));
        columns["RowVersion"].Type.ShouldBe("timestamp");
        columns["RowVersion"].Nullable.ShouldBeFalse();

        var primaryKey = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = PrimaryKeySql;
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                primaryKey.Add(reader.GetString(0));
            }
        }

        primaryKey.ShouldBe(["Name"]);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
