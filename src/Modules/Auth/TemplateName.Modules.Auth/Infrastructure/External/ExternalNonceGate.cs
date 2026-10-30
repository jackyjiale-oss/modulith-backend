using System.Collections.Concurrent;

namespace TemplateName.Modules.Auth.Infrastructure.External;

/// <summary>
/// The in-process half of the replay guard: an atomic add-if-absent of a (hashed) nonce key with its expiry, so exactly one of any number of
/// concurrent claims of a key wins within this process, whatever the cache does. An expired key is taken over atomically (compare and swap).
/// The gate is bounded: it never holds more than <see cref="Capacity"/> keys. Expired keys are pruned every <see cref="PruneEvery"/> entries
/// and whenever the gate is full; if it is still full of live keys, a new claim is refused rather than a live key evicted, which would
/// reopen that nonce to replay. Only fully verified proofs reach the gate, so filling it needs that many valid provider tokens.
/// </summary>
internal sealed class ExternalNonceGate(TimeProvider timeProvider, int capacity = ExternalNonceGate.DefaultCapacity)
{
    /// <summary>The most keys held at once: about 25 MB at worst, and 300 sign-ins a second for a 5.5-minute window.</summary>
    internal const int DefaultCapacity = 100_000;

    /// <summary>How many new keys pass between two sweeps of expired keys, so memory follows the traffic, not the cap.</summary>
    internal const int PruneEvery = 1024;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _expiries = new(StringComparer.Ordinal);
    private int _count;
    private int _addsSincePrune;

    /// <summary>The most keys the gate holds.</summary>
    public int Capacity { get; } = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The capacity must be positive.");

    /// <summary>The keys held now (live or not yet pruned).</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Holds <paramref name="key"/> for <paramref name="window"/> from now, unless a live claim already holds it or the gate is full.</summary>
    public NonceGateResult TryEnter(string key, TimeSpan window)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var now = timeProvider.GetUtcNow();
        var expires = now + window;

        // Each pass either decides or saw another caller change the key (add, take-over or prune), so the loop ends.
        while (true)
        {
            if (_expiries.TryGetValue(key, out var held))
            {
                if (held > now)
                {
                    return NonceGateResult.Spent;
                }

                // Expired: take it over. Of several racing callers only one swap succeeds; the others see the new, live expiry next pass.
                if (_expiries.TryUpdate(key, expires, held))
                {
                    return NonceGateResult.Entered;
                }

                continue;
            }

            // Reserve a slot first, so the gate never holds more than Capacity keys even under concurrent adds.
            if (Interlocked.Increment(ref _count) > Capacity)
            {
                Prune(now);
                if (Volatile.Read(ref _count) > Capacity)
                {
                    Interlocked.Decrement(ref _count);
                    return NonceGateResult.Full;
                }
            }

            if (!_expiries.TryAdd(key, expires))
            {
                // Another caller added the key between the lookup and here: give the slot back and look again (it is live, so Spent).
                Interlocked.Decrement(ref _count);
                continue;
            }

            if (Interlocked.Increment(ref _addsSincePrune) >= PruneEvery)
            {
                Interlocked.Exchange(ref _addsSincePrune, 0);
                Prune(now);
            }

            return NonceGateResult.Entered;
        }
    }

    /// <summary>Removes every key whose window has ended (only if it was not taken over meanwhile).</summary>
    private void Prune(DateTimeOffset now)
    {
        foreach (var entry in _expiries)
        {
            if (entry.Value <= now && _expiries.TryRemove(entry))
            {
                Interlocked.Decrement(ref _count);
            }
        }
    }
}
