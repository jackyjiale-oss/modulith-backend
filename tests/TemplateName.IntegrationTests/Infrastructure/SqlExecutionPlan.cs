using System.Data.Common;
using System.Xml.Linq;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>Reads the plan SQL Server would use for a statement, without running it, so a test can check that an index serves a predicate.</summary>
internal static class SqlExecutionPlan
{
    private const int DeclaredBatch = 1000;

    /// <summary>
    /// The plan of a cleanup job's <c>DELETE TOP (@Batch)</c> statement (<c>@Batch</c> and <c>@Cutoff</c> declared, as the job binds them),
    /// compiled by <c>SET SHOWPLAN_XML ON</c> and not run. Opens <paramref name="connection"/> if it is closed.
    /// </summary>
    public static Task<string> OfDeleteAsync(DbConnection connection, string deleteSql, CancellationToken cancellationToken)
        => PlanAsync(connection, deleteSql, cancellationToken);

    /// <summary>
    /// The same plan with the statement compiled for a given cutoff. The helper declares <c>@Batch</c> and <c>@Cutoff</c> as local variables,
    /// which SQL Server cannot sniff: it estimates each comparison with a flat guess (a third of the rows), which skews the estimate of a
    /// predicate with an <c>OR</c> or a subquery. The job binds them as parameters, whose values it sniffs, so
    /// <c>OPTION (OPTIMIZE FOR (@Batch = 1000, @Cutoff = ...))</c> gives the compile the values the job would bind and the plan is the one
    /// the job gets.
    /// </summary>
    /// <param name="connection">The connection to compile on; opened if it is closed.</param>
    /// <param name="deleteSql">The job's statement.</param>
    /// <param name="optimizeForCutoff">The <c>@Cutoff</c> the plan is compiled for (UTC); the batch size is the declared 1000.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    public static Task<string> OfDeleteAsync(DbConnection connection, string deleteSql, DateTime optimizeForCutoff, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deleteSql);

        var cutoff = optimizeForCutoff.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var hinted = deleteSql.TrimEnd().TrimEnd(';') + $" OPTION (OPTIMIZE FOR (@Batch = {DeclaredBatch}, @Cutoff = '{cutoff}'));";

        return PlanAsync(connection, hinted, cancellationToken);
    }

    /// <summary>The clustered index scans in <paramref name="plan"/> that read <paramref name="table"/> (as SQL Server names it, for example <c>[InboxMessages]</c>).</summary>
    public static List<XElement> ClusteredIndexScansOf(string plan, string table)
        => XDocument.Parse(plan).Descendants()
            .Where(element => element.Name.LocalName == "RelOp" && (string?)element.Attribute("PhysicalOp") == "Clustered Index Scan")
            .Where(scan => scan.Descendants().Any(element => element.Name.LocalName == "Object" && (string?)element.Attribute("Table") == table))
            .ToList();

    /// <summary>
    /// The plan SQL Server cached for the most recently run list statement (<c>SELECT TOP (@Take)</c>) whose text contains
    /// <paramref name="statementFragment"/>: the plan of the SQL an application really sent (a Dapper query, say) with the parameter
    /// values it sniffed, without the test having the SQL text. Fails if no cached statement matches. Needs <c>VIEW SERVER STATE</c>;
    /// opens <paramref name="connection"/> if it is closed.
    /// </summary>
    public static async Task<string> OfCachedStatementAsync(DbConnection connection, string statementFragment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(statementFragment);

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT TOP (1) CAST(plan_data.query_plan AS nvarchar(max))
            FROM sys.dm_exec_query_stats AS stats
            CROSS APPLY sys.dm_exec_sql_text(stats.sql_handle) AS statement_text
            CROSS APPLY sys.dm_exec_query_plan(stats.plan_handle) AS plan_data
            WHERE CHARINDEX(@Fragment, statement_text.text) > 0
                AND CHARINDEX(N'SELECT TOP (@Take)', statement_text.text) > 0
                AND CHARINDEX(N'dm_exec_query_stats', statement_text.text) = 0
            ORDER BY stats.last_execution_time DESC
            """;
        var fragment = command.CreateParameter();
        fragment.ParameterName = "@Fragment";
        fragment.Value = statementFragment;
        command.Parameters.Add(fragment);

        return await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidOperationException($"No cached plan for a list statement containing '{statementFragment}'.");
    }

    /// <summary>The physical operators of <paramref name="plan"/> (<c>Index Seek</c>, <c>Index Scan</c>, <c>Sort</c>, <c>Top N Sort</c>, <c>Key Lookup</c>, ...), in plan order.</summary>
    public static List<string> PhysicalOperatorsOf(string plan)
        => XDocument.Parse(plan).Descendants()
            .Where(element => element.Name.LocalName == "RelOp")
            .Select(element => (string?)element.Attribute("PhysicalOp") ?? string.Empty)
            .ToList();

    private static async Task<string> PlanAsync(DbConnection connection, string statement, CancellationToken cancellationToken)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await ExecuteAsync(connection, "SET SHOWPLAN_XML ON", cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"DECLARE @Batch int = {DeclaredBatch}; DECLARE @Cutoff datetime2(3) = '2025-12-25T00:00:00'; {statement}";
            return string.Concat(await ReadPlansAsync(command, cancellationToken));
        }
        finally
        {
            await ExecuteAsync(connection, "SET SHOWPLAN_XML OFF", cancellationToken);
        }
    }

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
