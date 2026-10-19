using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>Finds a secret that leaked into the database: scans every text column of every table of the test database.</summary>
internal static class DatabaseTextScanner
{
    /// <summary>
    /// Every <c>schema.table.column</c> of the test database (every module's schema) whose text holds one of <paramref name="values"/>,
    /// ignoring case. The values are letters, digits, <c>-</c>, <c>+</c> and <c>/</c>, none of which is a <c>LIKE</c> wildcard.
    /// </summary>
    internal static async Task<List<string>> ColumnsContainingAsync(
        IServiceProvider services,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var connection = scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        var columns = new List<(string Schema, string Table, string Column)>();
        await using (var list = connection.CreateCommand())
        {
            list.CommandText = """
                SELECT c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME
                FROM INFORMATION_SCHEMA.COLUMNS AS c
                INNER JOIN INFORMATION_SCHEMA.TABLES AS t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
                WHERE t.TABLE_TYPE = 'BASE TABLE' AND c.DATA_TYPE IN ('char', 'varchar', 'nchar', 'nvarchar', 'text', 'ntext', 'xml')
                """;
            await using var reader = await list.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        columns.ShouldContain(column => column.Schema == "auth" && column.Table == "AuthAuditLogs" && column.Column == "Details");
        columns.ShouldContain(column => column.Schema == "auth" && column.Table == "OutboxMessages" && column.Column == "Content");

        var hits = new List<string>();
        foreach (var (schema, table, column) in columns)
        {
            await using var command = connection.CreateCommand();
            var conditions = values.Select((_, index) => $"CAST([{column}] AS nvarchar(max)) LIKE @p{index}");
            command.CommandText = $"SELECT COUNT_BIG(*) FROM [{schema}].[{table}] WHERE {string.Join(" OR ", conditions)}";
            for (var index = 0; index < values.Count; index++)
            {
                AddParameter(command, $"@p{index}", $"%{values[index]}%");
            }

            if ((long)(await command.ExecuteScalarAsync(cancellationToken))! > 0)
            {
                hits.Add($"{schema}.{table}.{column}");
            }
        }

        return hits;
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
