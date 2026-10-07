using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Data;

namespace TemplateName.Infrastructure.Common.Persistence;

/// <summary>Opens SQL Server connections to the <c>Database</c> connection string for read-side queries.</summary>
internal sealed class SqlConnectionFactory(IOptions<ConnectionStringsOptions> options) : IDbConnectionFactory
{
    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.Database);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
