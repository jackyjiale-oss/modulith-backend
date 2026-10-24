using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace TemplateName.Infrastructure.Common.Jobs;

/// <summary>Runs a <c>DELETE TOP (@Batch)</c> statement again and again, so a cleanup removes a backlog in bounded transactions.</summary>
internal static class BatchedDelete
{
    /// <summary>
    /// Runs <paramref name="sql"/> with <c>@Batch</c> = <paramref name="batchSize"/> and <c>@Cutoff</c> = <paramref name="cutoff"/> until a
    /// batch deletes fewer than <paramref name="batchSize"/> rows or <paramref name="maxBatches"/> batches have run. Each batch is its own
    /// statement, so its locks and log are released before the next starts. The statement runs with <c>NOCOUNT OFF</c> whatever the
    /// server or the session defaults to, because the loop reads the number of rows it reports (with <c>NOCOUNT ON</c> that is -1).
    /// </summary>
    /// <returns>The sum of the rows the statement reported for each batch.</returns>
    public static async Task<int> RunAsync(
        DbContext context,
        string sql,
        DateTime cutoff,
        int batchSize,
        int maxBatches,
        CancellationToken cancellationToken)
    {
        var statement = "SET NOCOUNT OFF;\n" + sql;
        var total = 0;
        for (var batch = 0; batch < maxBatches; batch++)
        {
            var deleted = await context.Database.ExecuteSqlRawAsync(
                statement,
                [
                    new SqlParameter("@Batch", SqlDbType.Int) { Value = batchSize },
                    new SqlParameter("@Cutoff", SqlDbType.DateTime2) { Scale = 3, Value = cutoff },
                ],
                cancellationToken);
            total += deleted;

            if (deleted < batchSize)
            {
                break;
            }
        }

        return total;
    }
}
