using System.Data.Common;
using TemplateName.Application.Common.Data;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>Opens connections through the real factory and counts them, so a test can tell whether a read reached the database.</summary>
public sealed class RecordingDbConnectionFactory(IDbConnectionFactory inner) : IDbConnectionFactory
{
    private int _openedCount;

    public int OpenedCount => Volatile.Read(ref _openedCount);

    public Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _openedCount);
        return inner.OpenConnectionAsync(cancellationToken);
    }
}
