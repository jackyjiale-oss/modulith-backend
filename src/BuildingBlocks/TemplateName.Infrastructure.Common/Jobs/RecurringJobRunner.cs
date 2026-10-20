using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Idempotency;

namespace TemplateName.Infrastructure.Common.Jobs;

/// <summary>
/// Runs the due recurring jobs registered with <c>AddRecurringJob</c>, one at a time, each under a lease on its row in
/// <c>platform.JobStates</c>, so instances that tick together never run a job twice. A claimed job runs in a scope of its own under a
/// token cancelled when the lease ends; its outcome and next due time (<c>finish + interval</c>, so a missed interval runs once, not in a
/// burst) are written by an update guarded by the lease it claimed, so a runner whose lease ran out never overwrites the outcome of the
/// runner that re-claimed the job. A job that throws is recorded as failed and does not stop the others.
/// </summary>
public sealed partial class RecurringJobRunner
{
    internal const string SucceededStatus = "Succeeded";
    internal const string FailedStatus = "Failed";

    // Creates the state row of each job that has none, due now. UPDLOCK + HOLDLOCK make two runners inserting together queue on the
    // key instead of both passing NOT EXISTS; a primary-key violation that still gets through is ignored.
    private const string InsertMissingSqlTemplate = """
        INSERT INTO {jobStates} (Name, NextRunAt)
        SELECT job.Name, @Now
        FROM OPENJSON(@Names) WITH (Name nvarchar(200) '$') AS job
        WHERE NOT EXISTS (SELECT 1 FROM {jobStates} AS state WITH (UPDLOCK, HOLDLOCK) WHERE state.Name = job.Name);
        """;

    // UPDLOCK + READPAST: concurrent claims skip each other's rows instead of waiting or taking the same one, as in the outbox. The
    // lease (LockedUntil) keeps the claimed row hidden after this statement commits; it expires if the claiming process dies. One job
    // per claim, the most overdue first, so each run gets the whole lease and instances ticking together share the due jobs.
    private const string ClaimSqlTemplate = """
        WITH due AS (
            SELECT TOP (1) *
            FROM {jobStates} WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE Name IN (SELECT Name FROM OPENJSON(@Names) WITH (Name nvarchar(200) '$'))
              AND NextRunAt <= @Now
              AND (LockedUntil IS NULL OR LockedUntil < @Now)
            ORDER BY NextRunAt, Name)
        UPDATE due SET LockedUntil = @LeaseUntil
        OUTPUT inserted.Name, inserted.LockedUntil;
        """;

    private const int UniqueConstraintViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlyList<RecurringJobRegistration> _registrations;
    private readonly JobsOptions _options;
    private readonly IOptionsMonitor<RecurringJobOptions> _jobOptions;
    private readonly RecurringJobMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RecurringJobRunner> _logger;
    private IReadOnlyList<RegisteredJob>? _registry;
    private string? _insertMissingSql;
    private string? _claimSql;

    internal RecurringJobRunner(
        IServiceScopeFactory scopeFactory,
        IEnumerable<RecurringJobRegistration> registrations,
        IOptions<JobsOptions> options,
        IOptionsMonitor<RecurringJobOptions> jobOptions,
        RecurringJobMetrics metrics,
        TimeProvider timeProvider,
        ILogger<RecurringJobRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _registrations = [.. registrations];
        _options = options.Value;
        _jobOptions = jobOptions;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Claims and runs, one after another, every registered job that is enabled, accepted by <paramref name="include"/> (all jobs when
    /// null) and due, each at most once.
    /// </summary>
    /// <returns>The number of jobs run, failed runs included.</returns>
    /// <exception cref="InvalidOperationException">Two registered jobs share a name, or a job's name or default interval is invalid.</exception>
    public async Task<int> RunDueAsync(Func<string, bool>? include, CancellationToken cancellationToken)
    {
        var registry = await GetRegistryAsync();

        var eligible = new Dictionary<string, EligibleJob>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in registry)
        {
            if (include is not null && !include(job.Name))
            {
                continue;
            }

            // A job whose own options cannot be read is skipped (and logged); the others still run.
            if (TryGetJobOptions(job.Name, out var jobOptions) && jobOptions.Enabled)
            {
                eligible.Add(job.Name, new EligibleJob(job, jobOptions.IntervalOr(job.DefaultInterval)));
            }
        }

        if (eligible.Count == 0)
        {
            return 0;
        }

        await InsertMissingStatesAsync(eligible.Keys, cancellationToken);

        var runCount = 0;
        while (eligible.Count > 0)
        {
            var claimed = await ClaimNextAsync(eligible.Keys, cancellationToken);
            if (claimed is null || !eligible.Remove(claimed.Name, out var job))
            {
                break;
            }

            await RunClaimedAsync(job, claimed, cancellationToken);
            runCount++;
        }

        return runCount;
    }

    /// <summary>
    /// Builds the registry and reads every registered job's <c>Jobs:{Name}</c> options, so a host with a duplicate or invalid job
    /// name, or a job setting that cannot be bound or fails validation, does not start (<c>RecurringJobBackgroundService</c> calls it on
    /// start, whether or not <c>Jobs:Enabled</c> is set).
    /// </summary>
    /// <exception cref="InvalidOperationException">The registry cannot be built, or one or more jobs' options are invalid; the message names each job's section.</exception>
    internal async Task ValidateJobsAsync()
    {
        var registry = await GetRegistryAsync();

        var failures = new List<string>();
        foreach (var job in registry)
        {
            try
            {
                _ = _jobOptions.Get(job.Name);
            }
            catch (Exception exception) when (IsOptionsFailure(exception))
            {
                failures.Add($"{JobsOptions.SectionName}:{job.Name}: {exception.Message}");
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException($"The options of recurring jobs are invalid. {string.Join(" ", failures)}");
        }
    }

    /// <summary>Creates the runner the way the container does; tests use it for a second runner over the same database.</summary>
    internal static RecurringJobRunner Create(IServiceProvider services) => new(
        services.GetRequiredService<IServiceScopeFactory>(),
        services.GetServices<RecurringJobRegistration>(),
        services.GetRequiredService<IOptions<JobsOptions>>(),
        services.GetRequiredService<IOptionsMonitor<RecurringJobOptions>>(),
        services.GetRequiredService<RecurringJobMetrics>(),
        services.GetRequiredService<TimeProvider>(),
        services.GetRequiredService<ILogger<RecurringJobRunner>>());

    /// <summary>Reads each registered job's name and default interval once; a registry that fails to build is not kept.</summary>
    private async Task<IReadOnlyList<RegisteredJob>> GetRegistryAsync()
    {
        if (Volatile.Read(ref _registry) is { } registry)
        {
            return registry;
        }

        var jobs = new List<RegisteredJob>(_registrations.Count);
        var typesByName = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            foreach (var registration in _registrations)
            {
                var job = (IRecurringJob)scope.ServiceProvider.GetRequiredService(registration.JobType);
                var name = job.Name;
                if (!IsValidName(name))
                {
                    throw new InvalidOperationException(
                        $"Recurring job {registration.JobType.FullName} has the name '{name}'; a job name is 1 to {JobStateRecord.MaxNameLength} "
                        + "ASCII letters and digits starting with a letter, so it also works as a configuration and environment-variable key.");
                }

                if (job.DefaultInterval <= TimeSpan.Zero)
                {
                    throw new InvalidOperationException($"Recurring job {name} has a default interval of {job.DefaultInterval}; it must be positive.");
                }

                if (typesByName.TryGetValue(name, out var other))
                {
                    throw new InvalidOperationException(
                        $"Recurring jobs {other.FullName} and {registration.JobType.FullName} are both named '{name}'; job names must be unique.");
                }

                typesByName.Add(name, registration.JobType);
                jobs.Add(new RegisteredJob(name, registration.JobType, job.DefaultInterval));
            }
        }

        // Two callers racing here build the same registry; either result is kept.
        return Interlocked.CompareExchange<IReadOnlyList<RegisteredJob>?>(ref _registry, jobs, null) ?? jobs;
    }

    private async Task InsertMissingStatesAsync(IEnumerable<string> names, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        _insertMissingSql ??= BuildSql(InsertMissingSqlTemplate, context);

        try
        {
            await context.Database.ExecuteSqlRawAsync(
                _insertMissingSql,
                [NamesParameter(names), NowParameter(_timeProvider.GetUtcNow().UtcDateTime)],
                cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is UniqueConstraintViolation or UniqueIndexViolation)
        {
            // Another runner created the rows first; the claim below works on them.
        }
    }

    private async Task<ClaimedJob?> ClaimNextAsync(IEnumerable<string> names, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        _claimSql ??= BuildSql(ClaimSqlTemplate, context);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var claimed = await context.Database
            .SqlQueryRaw<ClaimedJob>(
                _claimSql,
                NamesParameter(names),
                NowParameter(now),
                new SqlParameter("@LeaseUntil", SqlDbType.DateTime2) { Scale = 3, Value = now + _options.LeaseDuration })
            .ToListAsync(cancellationToken);

        return claimed.SingleOrDefault();
    }

    private async Task RunClaimedAsync(EligibleJob eligible, ClaimedJob claimed, CancellationToken cancellationToken)
    {
        var name = eligible.Job.Name;
        var startedAt = _timeProvider.GetUtcNow().UtcDateTime;
        var startTimestamp = _timeProvider.GetTimestamp();
        var leaseLeft = claimed.LockedUntil - startedAt;

        using var leaseEnd = new CancellationTokenSource(leaseLeft > TimeSpan.Zero ? leaseLeft : TimeSpan.Zero, _timeProvider);
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, leaseEnd.Token);

        JobResult? result = null;
        string? error = null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var job = (IRecurringJob)scope.ServiceProvider.GetRequiredService(eligible.Job.JobType);
            result = await job.RunAsync(runCancellation.Token);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A host that is stopping lets the exception through; the lease then expires and another run picks the job up.
            if (leaseEnd.IsCancellationRequested)
            {
                error = $"The run was cancelled when its lease of {_options.LeaseDuration} ran out.";
                LogLeaseEnded(_logger, exception, name, _options.LeaseDuration);
            }
            else
            {
                error = $"{exception.GetType().FullName}: {exception.Message}";
                LogJobFailed(_logger, exception, name);
            }
        }

        var finishedAt = _timeProvider.GetUtcNow().UtcDateTime;
        var duration = _timeProvider.GetElapsedTime(startTimestamp);
        _metrics.RecordRun(name, succeeded: result is not null, duration, result?.Processed ?? 0);

        var updated = await FinishAsync(name, claimed.LockedUntil, startedAt, finishedAt, eligible.Interval, result, error, cancellationToken);
        if (updated == 0)
        {
            LogLeaseLost(_logger, name);
        }
        else if (result is { } succeeded)
        {
            LogJobSucceeded(_logger, name, succeeded.Processed, duration);
        }
    }

    private async Task<int> FinishAsync(
        string name,
        DateTime claimedLease,
        DateTime startedAt,
        DateTime finishedAt,
        TimeSpan interval,
        JobResult? result,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var status = result is null ? FailedStatus : SucceededStatus;
        var processed = result?.Processed;
        var lastError = error is null ? null : Truncate(error, JobStateRecord.MaxErrorLength);
        var nextRunAt = finishedAt + interval;

        // Only the holder of the lease this runner claimed may record the run.
        return await context.JobStates
            .Where(state => state.Name == name && state.LockedUntil == claimedLease)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(state => state.LockedUntil, (DateTime?)null)
                    .SetProperty(state => state.NextRunAt, nextRunAt)
                    .SetProperty(state => state.LastStartedAt, startedAt)
                    .SetProperty(state => state.LastFinishedAt, finishedAt)
                    .SetProperty(state => state.LastStatus, status)
                    .SetProperty(state => state.LastError, lastError)
                    .SetProperty(state => state.LastProcessed, processed),
                cancellationToken);
    }

    private bool TryGetJobOptions(string name, [NotNullWhen(true)] out RecurringJobOptions? jobOptions)
    {
        try
        {
            jobOptions = _jobOptions.Get(name);
            return true;
        }
        catch (Exception exception) when (IsOptionsFailure(exception))
        {
            LogJobOptionsInvalid(_logger, exception, name, $"{JobsOptions.SectionName}:{name}");
            jobOptions = null;
            return false;
        }
    }

    /// <summary>A value the binder cannot convert (<see cref="InvalidOperationException"/>) or one that fails validation.</summary>
    private static bool IsOptionsFailure(Exception exception) => exception is OptionsValidationException or InvalidOperationException;

    private static string BuildSql(string template, PlatformDbContext context)
    {
        var entityType = context.Model.FindEntityType(typeof(JobStateRecord))!;
        var jobStates = context.GetService<ISqlGenerationHelper>().DelimitIdentifier(entityType.GetTableName()!, entityType.GetSchema());
        return template.Replace("{jobStates}", jobStates, StringComparison.Ordinal);
    }

    private static SqlParameter NamesParameter(IEnumerable<string> names)
        => new("@Names", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(names) };

    private static SqlParameter NowParameter(DateTime now) => new("@Now", SqlDbType.DateTime2) { Scale = 3, Value = now };

    private static bool IsValidName(string? name)
        => !string.IsNullOrEmpty(name)
            && name.Length <= JobStateRecord.MaxNameLength
            && char.IsAsciiLetter(name[0])
            && name.All(char.IsAsciiLetterOrDigit);

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    [LoggerMessage(Level = LogLevel.Information, Message = "Recurring job {JobName} processed {Processed} in {Duration}")]
    private static partial void LogJobSucceeded(ILogger logger, string jobName, int processed, TimeSpan duration);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recurring job {JobName} failed")]
    private static partial void LogJobFailed(ILogger logger, Exception exception, string jobName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recurring job {JobName} was cancelled when its lease of {LeaseDuration} ran out")]
    private static partial void LogLeaseEnded(ILogger logger, Exception exception, string jobName, TimeSpan leaseDuration);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recurring job {JobName} was skipped: its options in {ConfigurationSection} are invalid")]
    private static partial void LogJobOptionsInvalid(ILogger logger, Exception exception, string jobName, string configurationSection);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recurring job {JobName} lost its lease to another runner; that runner's outcome stands")]
    private static partial void LogLeaseLost(ILogger logger, string jobName);

    /// <summary>A registered job as the registry knows it.</summary>
    private sealed record RegisteredJob(string Name, Type JobType, TimeSpan DefaultInterval);

    /// <summary>A registered job this pass may run, with the interval that applies to it.</summary>
    private sealed record EligibleJob(RegisteredJob Job, TimeSpan Interval);

    /// <summary>The columns the claim returns; <see cref="LockedUntil"/> is the lease this runner holds.</summary>
    private sealed record ClaimedJob(string Name, DateTime LockedUntil);
}
