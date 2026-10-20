using System.ComponentModel.DataAnnotations;

namespace TemplateName.Infrastructure.Common.Jobs;

/// <summary>
/// One job's own settings (section <c>Jobs:{Name}</c>), read as the named options of the job's name
/// (<c>IOptionsMonitor&lt;RecurringJobOptions&gt;.Get(name)</c>) and validated when they are first read.
/// </summary>
internal sealed class RecurringJobOptions
{
    /// <summary>Lets the scheduler run the job. A disabled job is never claimed and gets no state row.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The time from the end of one run to the next; null keeps the job's <see cref="IRecurringJob.DefaultInterval"/>.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "365.00:00:00")]
    public TimeSpan? Interval { get; set; }

    /// <summary>The configured interval, else <paramref name="defaultInterval"/>.</summary>
    public TimeSpan IntervalOr(TimeSpan defaultInterval) => Interval ?? defaultInterval;
}
