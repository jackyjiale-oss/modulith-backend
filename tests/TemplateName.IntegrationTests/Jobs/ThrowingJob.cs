using TemplateName.Infrastructure.Common.Jobs;

namespace TemplateName.IntegrationTests.Jobs;

/// <summary>A recurring job whose every run fails with <see cref="ErrorMessage"/>.</summary>
public sealed class ThrowingJob : IRecurringJob
{
    public const string JobName = "TestThrowing";
    public const string ErrorMessage = "The throwing test job always fails.";

    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    public string Name => JobName;

    public TimeSpan DefaultInterval => Interval;

    public Task<JobResult> RunAsync(CancellationToken cancellationToken) => throw new InvalidOperationException(ErrorMessage);
}
