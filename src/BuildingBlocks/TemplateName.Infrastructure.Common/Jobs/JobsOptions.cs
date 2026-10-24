using System.ComponentModel.DataAnnotations;

namespace TemplateName.Infrastructure.Common.Jobs;

/// <summary>
/// The recurring-job scheduler (section <c>Jobs</c>). Each job also reads its own section <c>Jobs:{Name}</c> (<c>Enabled</c>,
/// <c>Interval</c>; see <see cref="RecurringJobOptions"/>); a section that names no registered job is ignored.
/// </summary>
public sealed class JobsOptions
{
    internal const string SectionName = "Jobs";

    /// <summary>Runs the scheduler's background service. Tests turn it off and call <c>RecurringJobRunner.RunDueAsync</c> themselves.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the scheduler looks for due jobs.</summary>
    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a claimed run keeps other instances off the job; a run still going when it ends is cancelled.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "1.00:00:00")]
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>The most batches a batched job (a cleanup) runs in one run, so a run stays bounded and a backlog drains over several.</summary>
    [Range(1, 10000)]
    public int MaxBatchesPerRun { get; set; } = 100;
}
