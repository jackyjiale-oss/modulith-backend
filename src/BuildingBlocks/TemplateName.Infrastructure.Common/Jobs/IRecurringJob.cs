namespace TemplateName.Infrastructure.Common.Jobs;

/// <summary>
/// Work that runs on a fixed interval on one instance at a time, registered with <c>AddRecurringJob</c>. The scheduler resolves the job
/// in a scope of its own for every run.
/// </summary>
public interface IRecurringJob
{
    /// <summary>
    /// The job's unique name: its row in <c>platform.JobStates</c> and its configuration section <c>Jobs:{Name}</c>. PascalCase letters
    /// and digits only (no dots), so it also works as an environment-variable key, for example <c>AuthRefreshTokensCleanup</c>.
    /// </summary>
    string Name { get; }

    /// <summary>The time from the end of one run to the next, unless <c>Jobs:{Name}:Interval</c> sets another.</summary>
    TimeSpan DefaultInterval { get; }

    /// <summary>
    /// Runs the job once. The token is cancelled when the run's lease ends (<c>Jobs:LeaseDuration</c>) or the host stops; a run that
    /// throws is recorded as failed and tried again after the interval.
    /// </summary>
    Task<JobResult> RunAsync(CancellationToken cancellationToken);
}
