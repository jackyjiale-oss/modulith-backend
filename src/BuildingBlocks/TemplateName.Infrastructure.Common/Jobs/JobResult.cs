namespace TemplateName.Infrastructure.Common.Jobs;

/// <summary>What one run of an <see cref="IRecurringJob"/> did: <paramref name="Processed"/> is the number of rows (or items) it handled.</summary>
public readonly record struct JobResult(int Processed);
