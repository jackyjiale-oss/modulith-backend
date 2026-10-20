using System.Data.Common;
using System.Xml.Linq;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>Reads the plan SQL Server would use for a statement, without running it, so a test can check that an index serves a predicate.</summary>
internal static class SqlExecutionPlan
{
    /// <summary>
    /// The plan of a cleanup job's <c>DELETE TOP (@Batch)</c> statement (<c>@Batch</c> and <c>@Cutoff</c> declared, as the job binds them),
    /// compiled by <c>SET SHOWPLAN_XML ON</c> and not run. Opens <paramref name="connection"/> if it is closed.
    /// </summary>
    public static async Task<string> OfDeleteAsync(DbConnection connection, string deleteSql, CancellationToken cancellationToken)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await ExecuteAsync(connection, "SET SHOWPLAN_XML ON", cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"DECLARE @Batch int = 1000; DECLARE @Cutoff datetime2(3) = '2025-12-25T00:00:00'; {deleteSql}";
            return string.Concat(await ReadPlansAsync(command, cancellationToken));
        }
        finally
        {
            await ExecuteAsync(connection, "SET SHOWPLAN_XML OFF", cancellationToken);
        }
    }

    /// <summary>The clustered index scans in <paramref name="plan"/> that read <paramref name="table"/> (as SQL Server names it, for example <c>[InboxMessages]</c>).</summary>
    public static List<XElement> ClusteredIndexScansOf(string plan, string table)
        => XDocument.Parse(plan).Descendants()
            .Where(element => element.Name.LocalName == "RelOp" && (string?)element.Attribute("PhysicalOp") == "Clustered Index Scan")
            .Where(scan => scan.Descendants().Any(element => element.Name.LocalName == "Object" && (string?)element.Attribute("Table") == table))
            .ToList();

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<string>> ReadPlansAsync(DbCommand command, CancellationToken cancellationToken)
    {
        var plans = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        do
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                plans.Add(reader.GetString(0));
            }
        }
        while (await reader.NextResultAsync(cancellationToken));

        return plans;
    }
}
