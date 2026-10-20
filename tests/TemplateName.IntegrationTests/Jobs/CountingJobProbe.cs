using TemplateName.Infrastructure.Common.Jobs;

namespace TemplateName.IntegrationTests.Jobs;

/// <summary>
/// Counts the runs of <see cref="CountingJob"/> on every host and controls them: a test can hold the next run inside the job (until
/// <see cref="Release"/>, or until the run's token is cancelled unless told to ignore it) and act while a run is in progress through
/// <see cref="DuringRun"/>. Each run reports its own number (1 for the first) as <see cref="JobResult.Processed"/>.
/// </summary>
public sealed class CountingJobProbe
{
    private readonly Lock _lock = new();
    private int _runCount;
    private TaskCompletionSource? _entered;
    private TaskCompletionSource? _release;
    private bool _isArmed;
    private bool _ignoresCancellation;

    /// <summary>How many runs have started, finished or not.</summary>
    public int RunCount => Volatile.Read(ref _runCount);

    /// <summary>Called inside every run, before the run waits on a hold; for example to move the test clock.</summary>
    public Action? DuringRun { get; set; }

    /// <summary>
    /// Arms the hold for the next run; the returned task completes once that run is inside the job. With
    /// <paramref name="ignoresCancellation"/> the held run waits for <see cref="Release"/> even after its token is cancelled.
    /// </summary>
    public Task HoldNextRun(bool ignoresCancellation = false)
    {
        lock (_lock)
        {
            _entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _isArmed = true;
            _ignoresCancellation = ignoresCancellation;
            return _entered.Task;
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            _release?.TrySetResult();
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _isArmed = false;
            _release?.TrySetResult();
            _entered = null;
            _release = null;
            _runCount = 0;
            DuringRun = null;
        }
    }

    internal async Task<JobResult> RunAsync(CancellationToken cancellationToken)
    {
        var run = Interlocked.Increment(ref _runCount);
        DuringRun?.Invoke();

        Task? hold = null;
        lock (_lock)
        {
            if (_isArmed)
            {
                _isArmed = false;
                _entered!.TrySetResult();
                hold = _ignoresCancellation ? _release!.Task : _release!.Task.WaitAsync(cancellationToken);
            }
        }

        if (hold is not null)
        {
            await hold;
        }

        return new JobResult(run);
    }
}
