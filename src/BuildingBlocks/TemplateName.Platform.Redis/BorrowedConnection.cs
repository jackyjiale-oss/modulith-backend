using System.Reflection;
using System.Runtime.ExceptionServices;
using StackExchange.Redis;

namespace TemplateName.Platform.Redis;

/// <summary>
/// The instance's shared <see cref="IConnectionMultiplexer"/> as lent to a component that would close it when it is itself disposed:
/// SignalR's <c>RedisHubLifetimeManager</c> disposes the connection its <c>ConnectionFactory</c> returned (and the container disposes the
/// lifetime manager when the host stops), which would close the connection under the cache, the rate limiters and the readiness check.
/// Every member goes to the shared connection except <c>Dispose</c>, <c>DisposeAsync</c>, <c>Close</c> and <c>CloseAsync</c>, which do
/// nothing: the container owns the connection and disposes it once, when the host stops. A <see cref="DispatchProxy"/>, so it forwards
/// whatever members a later StackExchange.Redis adds to the interface; not sealed, because the proxy derives from it at run time.
/// </summary>
internal class BorrowedConnection : DispatchProxy
{
    private IConnectionMultiplexer? _connection;

    /// <summary>A connection that forwards to <paramref name="connection"/> and never closes or disposes it.</summary>
    internal static IConnectionMultiplexer Lend(IConnectionMultiplexer connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var borrowed = Create<IConnectionMultiplexer, BorrowedConnection>();
        ((BorrowedConnection)(object)borrowed)._connection = connection;
        return borrowed;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        switch (targetMethod.Name)
        {
            case nameof(IDisposable.Dispose):
            case nameof(IConnectionMultiplexer.Close):
                return null;
            case nameof(IAsyncDisposable.DisposeAsync):
                return ValueTask.CompletedTask;
            case nameof(IConnectionMultiplexer.CloseAsync):
                return Task.CompletedTask;
            default:
                break;
        }

        try
        {
            return targetMethod.Invoke(_connection, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            // The shared connection's own exception, with its stack trace, not the reflection wrapper.
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
