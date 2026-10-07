using System.Data.Common;

namespace TemplateName.Application.Common.Data;

/// <summary>Opens a database connection for read-side queries that bypass the EF Core change tracker.</summary>
public interface IDbConnectionFactory
{
    Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken);
}
