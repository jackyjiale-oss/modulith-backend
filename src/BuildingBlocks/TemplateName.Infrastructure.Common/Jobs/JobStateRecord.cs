namespace TemplateName.Infrastructure.Common.Jobs;

/// <summary>
/// The schedule and the last outcome of a recurring job (a row of <c>platform.JobStates</c>, keyed by the job's name). The scheduler
/// claims a due row by moving <see cref="LockedUntil"/> forward, so one instance runs a job at a time, and finishes it with an update that
/// matches the lease it claimed: the lease keeps a second writer off. SQL Server maintains <see cref="RowVersion"/>, but nothing reads it
/// today (the scheduler writes with <c>ExecuteUpdate</c>, outside the change tracker).
/// </summary>
internal sealed class JobStateRecord
{
    internal const int MaxNameLength = 200;
    internal const int MaxStatusLength = 20;
    internal const int MaxErrorLength = 2000;

    public required string Name { get; init; }

    /// <summary>The instant (UTC) from which the job is due.</summary>
    public DateTime NextRunAt { get; set; }

    /// <summary>While a run holds the lease, when the lease ends (UTC); null or in the past when no run holds it.</summary>
    public DateTime? LockedUntil { get; set; }

    public DateTime? LastStartedAt { get; set; }

    public DateTime? LastFinishedAt { get; set; }

    /// <summary>How the last run ended (<c>Succeeded</c> or <c>Failed</c>); null before the first run finishes.</summary>
    public string? LastStatus { get; set; }

    /// <summary>The last failure's message, cut to <see cref="MaxErrorLength"/>; null after a success.</summary>
    public string? LastError { get; set; }

    /// <summary>How many rows the last run handled (<c>JobResult.Processed</c>).</summary>
    public int? LastProcessed { get; set; }

    public byte[] RowVersion { get; private set; } = [];
}
