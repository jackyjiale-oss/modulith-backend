using Microsoft.Extensions.Time.Testing;
using TemplateName.Modules.Auth.Infrastructure.External;

namespace TemplateName.UnitTests.Auth.External;

/// <summary>The in-process half of the replay guard: atomic, expiring and bounded, on a fake clock.</summary>
public sealed class ExternalNonceGateTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5.5);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2031, 3, 1, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_key_is_spent_for_its_window_and_can_be_entered_again_once_it_has_ended()
    {
        var gate = new ExternalNonceGate(_time);

        gate.TryEnter("k", Window).ShouldBe(NonceGateResult.Entered);
        gate.TryEnter("k", Window).ShouldBe(NonceGateResult.Spent);

        _time.Advance(Window - TimeSpan.FromMilliseconds(1));
        gate.TryEnter("k", Window).ShouldBe(NonceGateResult.Spent);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        gate.TryEnter("k", Window).ShouldBe(NonceGateResult.Entered);
        gate.TryEnter("k", Window).ShouldBe(NonceGateResult.Spent);
        gate.Count.ShouldBe(1);
    }

    [Fact]
    public void A_full_gate_of_live_keys_refuses_new_keys_and_keeps_every_live_one()
    {
        var gate = new ExternalNonceGate(_time, capacity: 3);
        gate.TryEnter("a", Window).ShouldBe(NonceGateResult.Entered);
        gate.TryEnter("b", Window).ShouldBe(NonceGateResult.Entered);
        gate.TryEnter("c", Window).ShouldBe(NonceGateResult.Entered);

        gate.TryEnter("d", Window).ShouldBe(NonceGateResult.Full);

        // Fail closed: nothing live was evicted to make room, so none of the recent nonces can be replayed.
        gate.TryEnter("a", Window).ShouldBe(NonceGateResult.Spent);
        gate.TryEnter("b", Window).ShouldBe(NonceGateResult.Spent);
        gate.TryEnter("c", Window).ShouldBe(NonceGateResult.Spent);
        gate.Count.ShouldBe(3);
    }

    [Fact]
    public void A_full_gate_prunes_expired_keys_before_refusing()
    {
        var gate = new ExternalNonceGate(_time, capacity: 3);
        gate.TryEnter("a", Window);
        gate.TryEnter("b", Window);
        _time.Advance(TimeSpan.FromMinutes(1));
        gate.TryEnter("c", Window);
        _time.Advance(Window - TimeSpan.FromMinutes(1));

        // a and b have expired, c is still live.
        gate.TryEnter("d", Window).ShouldBe(NonceGateResult.Entered);
        gate.TryEnter("e", Window).ShouldBe(NonceGateResult.Entered);
        gate.TryEnter("f", Window).ShouldBe(NonceGateResult.Full);
        gate.TryEnter("c", Window).ShouldBe(NonceGateResult.Spent);
        gate.Count.ShouldBe(3);
    }

    [Fact]
    public void Distinct_keys_beyond_the_cap_never_grow_the_gate()
    {
        const int Capacity = 100;
        var gate = new ExternalNonceGate(_time, Capacity);

        var results = Enumerable.Range(0, 10_000).Select(index => gate.TryEnter($"key-{index}", Window)).ToArray();

        results.Take(Capacity).ShouldAllBe(result => result == NonceGateResult.Entered);
        results.Skip(Capacity).ShouldAllBe(result => result == NonceGateResult.Full);
        gate.Count.ShouldBe(Capacity);
        gate.TryEnter("key-0", Window).ShouldBe(NonceGateResult.Spent);
    }

    [Fact]
    public void Expired_keys_are_swept_as_traffic_goes_by_so_memory_follows_the_traffic()
    {
        var gate = new ExternalNonceGate(_time);
        for (var index = 0; index < ExternalNonceGate.PruneEvery - 1; index++)
        {
            gate.TryEnter($"old-{index}", Window).ShouldBe(NonceGateResult.Entered);
        }

        _time.Advance(Window);
        gate.TryEnter("new", Window).ShouldBe(NonceGateResult.Entered);

        gate.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_entries_of_one_key_let_exactly_one_in_every_round()
    {
        var gate = new ExternalNonceGate(_time);

        for (var round = 0; round < 200; round++)
        {
            var key = $"key-{round}";
            var results = await ReleasedTogetherAsync(64, () => gate.TryEnter(key, Window));

            results.Count(result => result == NonceGateResult.Entered).ShouldBe(1, $"round {round}");
            results.Count(result => result == NonceGateResult.Spent).ShouldBe(63, $"round {round}");
        }
    }

    [Fact]
    public async Task Concurrent_take_overs_of_an_expired_key_let_exactly_one_in_every_round()
    {
        var gate = new ExternalNonceGate(_time);

        for (var round = 0; round < 200; round++)
        {
            gate.TryEnter("key", Window).ShouldBe(round == 0 ? NonceGateResult.Entered : NonceGateResult.Spent);
            _time.Advance(Window);

            var results = await ReleasedTogetherAsync(64, () => gate.TryEnter("key", Window));

            results.Count(result => result == NonceGateResult.Entered).ShouldBe(1, $"round {round}");
        }

        gate.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_cap_holds_under_concurrent_distinct_keys()
    {
        var gate = new ExternalNonceGate(_time, capacity: 10);
        var next = 0;

        var results = await ReleasedTogetherAsync(64, () => gate.TryEnter($"key-{Interlocked.Increment(ref next)}", Window));

        results.Count(result => result == NonceGateResult.Entered).ShouldBe(10);
        results.Count(result => result == NonceGateResult.Full).ShouldBe(54);
        gate.Count.ShouldBe(10);
    }

    [Fact]
    public void A_capacity_that_is_not_positive_is_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ExternalNonceGate(_time, capacity: 0));
    }

    /// <summary>Runs <paramref name="enter"/> on <paramref name="callers"/> pool threads held on one gate and released together.</summary>
    private static async Task<NonceGateResult[]> ReleasedTogetherAsync(int callers, Func<NonceGateResult> enter)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, callers)
            .Select(_ => Task.Run(async () =>
            {
                await release.Task;
                return enter();
            }))
            .ToArray();
        release.SetResult();
        return await Task.WhenAll(calls);
    }
}
