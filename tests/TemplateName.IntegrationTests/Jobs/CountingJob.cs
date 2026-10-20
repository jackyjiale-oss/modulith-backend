using TemplateName.Infrastructure.Common.Jobs;

namespace TemplateName.IntegrationTests.Jobs;

/// <summary>A recurring job that does nothing but let <see cref="CountingJobProbe"/> count and control its runs.</summary>
public sealed class CountingJob(CountingJobProbe probe) : IRecurringJob
{
    public const string JobName = "TestCounting";

    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public string Name => JobName;

    public TimeSpan DefaultInterval => Interval;

    public Task<JobResult> RunAsync(CancellationToken cancellationToken) => probe.RunAsync(cancellationToken);
}
