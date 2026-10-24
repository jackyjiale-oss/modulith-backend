using NSubstitute;
using StackExchange.Redis;

namespace TemplateName.UnitTests.Platform;

/// <summary>
/// A substitute <see cref="IDatabase"/> (and the <see cref="IConnectionMultiplexer"/> that hands it out) for the Redis rate limiter. It
/// answers every script evaluation with <see cref="Count"/> and <see cref="TimeToLiveMilliseconds"/>, the pair the limiter's script
/// returns; it counts each evaluation and keeps the keys and arguments of the last one. While <see cref="Fails"/> is set an evaluation
/// fails the way an unreachable Redis does (a faulted task with a <see cref="RedisConnectionException"/>); while <see cref="Hang"/> is set
/// it never finishes, like a Redis that stopped answering.
/// </summary>
internal sealed class ScriptedRedisDatabase
{
    private int _calls;

    public ScriptedRedisDatabase()
    {
        Database = Substitute.For<IDatabase>();
        Database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .Returns(call => Evaluate(call.ArgAt<string>(0), call.ArgAt<RedisKey[]?>(1), call.ArgAt<RedisValue[]?>(2)));
        Multiplexer = Substitute.For<IConnectionMultiplexer>();
        Multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(Database);
    }

    public IDatabase Database { get; }

    public IConnectionMultiplexer Multiplexer { get; }

    public bool Fails { get; set; }

    public bool Hang { get; set; }

    /// <summary>The count after the increment that the next evaluation answers.</summary>
    public long Count { get; set; } = 1;

    /// <summary>The key's remaining time to live, in milliseconds, that the next evaluation answers.</summary>
    public long TimeToLiveMilliseconds { get; set; } = 60_000;

    public int Calls => Volatile.Read(ref _calls);

    public string? LastScript { get; private set; }

    public RedisKey[]? LastKeys { get; private set; }

    public RedisValue[]? LastValues { get; private set; }

    private Task<RedisResult> Evaluate(string script, RedisKey[]? keys, RedisValue[]? values)
    {
        Interlocked.Increment(ref _calls);
        LastScript = script;
        LastKeys = keys;
        LastValues = values;

        if (Hang)
        {
            return new TaskCompletionSource<RedisResult>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }

        if (Fails)
        {
            return Task.FromException<RedisResult>(new RedisConnectionException(
                ConnectionFailureType.UnableToConnect,
                CommandFlags.None,
                "No connection is active/available to service this operation.",
                innerException: null,
                CommandStatus.Unknown));
        }

        return Task.FromResult(RedisResult.Create([RedisResult.Create(Count), RedisResult.Create(TimeToLiveMilliseconds)]));
    }
}
