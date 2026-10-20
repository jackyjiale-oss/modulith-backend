using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.TrustedDevices;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.Modules.Auth.Infrastructure.Persistence;
using TemplateName.Modules.Auth.Infrastructure.Retention;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// The Auth cleanup jobs (ADR 0028): for each table the rows either side of the cutoff (a row goes only when strictly older), what stays
/// whatever its age (live sessions' unexpired tokens, a used token inside its lifetime, audit logs by default), that a session takes its
/// tokens with it, that the refresh-token reuse detection still works after a cleanup, and that each predicate uses its index.
/// </summary>
public sealed class AuthCleanupJobTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string LoginRoute = "/api/v1/auth/login";
    private const string RefreshRoute = "/api/v1/auth/token/refresh";
    private const string Email = "cleanup@example.com";

    private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);
    private static readonly TimeSpan SevenDays = TimeSpan.FromDays(7);
    private static readonly TimeSpan ThirtyDays = TimeSpan.FromDays(30);
    private static readonly TimeSpan NinetyDays = TimeSpan.FromDays(90);
    private static readonly TimeSpan OneYear = TimeSpan.FromDays(365);
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private static readonly string[] JobNames =
    [
        VerificationCodesCleanupJob.JobName,
        RefreshTokensCleanupJob.JobName,
        UserSessionsCleanupJob.JobName,
        MfaChallengesCleanupJob.JobName,
        TrustedDevicesCleanupJob.JobName,
        AuditLogsCleanupJob.JobName,
    ];

    private Guid? _userId;

    public static TheoryData<string> AllJobNames => [.. JobNames];

    public static TheoryData<string, bool> AllJobNamesWithOffValues
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (var name in JobNames)
            {
                data.Add(name, true);
                data.Add(name, false);
            }

            return data;
        }
    }

    private DateTimeOffset Now => Factory.Time.GetUtcNow();

    [Fact]
    public async Task The_six_auth_cleanup_jobs_are_registered_with_their_intervals()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetServices<RecurringJobRegistration>()
            .Select(registration => (IRecurringJob)scope.ServiceProvider.GetRequiredService(registration.JobType))
            .Where(job => job.Name.StartsWith("Auth", StringComparison.Ordinal) && job.Name.EndsWith("Cleanup", StringComparison.Ordinal))
            .Where(job => !job.Name.Contains("Outbox", StringComparison.Ordinal))
            .ToDictionary(job => job.Name, job => job.DefaultInterval);

        jobs.ShouldBe(
            new Dictionary<string, TimeSpan>
            {
                ["AuthVerificationCodesCleanup"] = TimeSpan.FromHours(1),
                ["AuthRefreshTokensCleanup"] = OneDay,
                ["AuthSessionsCleanup"] = OneDay,
                ["AuthMfaChallengesCleanup"] = TimeSpan.FromHours(1),
                ["AuthTrustedDevicesCleanup"] = OneDay,
                ["AuthAuditLogsCleanup"] = OneDay,
            },
            ignoreOrder: true);
        jobs.Keys.ShouldBe(JobNames, ignoreOrder: true);
    }

    [Fact]
    public async Task The_scheduler_runs_a_cleanup_job_registered_by_the_Auth_module()
    {
        var code = await SeedVerificationCodeAsync(Now - SevenDays - TimeSpan.FromHours(1));
        var runner = Factory.Services.GetRequiredService<RecurringJobRunner>();

        (await runner.RunDueAsync(name => name == VerificationCodesCleanupJob.JobName, Ct)).ShouldBe(1);

        (await VerificationCodeIdsAsync()).ShouldNotContain(code);
    }

    [Fact]
    public async Task Verification_codes_expired_more_than_seven_days_ago_go_and_a_code_expired_exactly_seven_days_ago_stays()
    {
        var exactly = await SeedVerificationCodeAsync(Now - SevenDays);
        var justOver = await SeedVerificationCodeAsync(Now - SevenDays - OneSecond);
        var pending = await SeedVerificationCodeAsync(Now + OneDay);

        var result = await RunAsync(VerificationCodesCleanupJob.JobName);

        result.Processed.ShouldBe(1);
        var remaining = await VerificationCodeIdsAsync();
        remaining.ShouldBe([exactly, pending], ignoreOrder: true);
        remaining.ShouldNotContain(justOver);
    }

    [Fact]
    public async Task Refresh_tokens_expired_more_than_thirty_days_ago_go_and_a_token_expired_exactly_thirty_days_ago_stays()
    {
        var session = await SeedSessionAsync(
            sessionExpiresAt: Now + OneDay,
            tokenExpiries: [Now - ThirtyDays, Now - ThirtyDays - OneSecond, Now + OneDay]);

        var result = await RunAsync(RefreshTokensCleanupJob.JobName);

        result.Processed.ShouldBe(1);
        (await TokenIdsAsync()).ShouldBe([session.TokenIds[0], session.TokenIds[2]], ignoreOrder: true);
        (await SessionIdsAsync()).ShouldBe([session.SessionId]);
    }

    [Fact]
    public async Task Unexpired_tokens_of_a_live_session_stay_and_expired_ones_of_the_same_session_go()
    {
        var live = await SeedSessionAsync(
            sessionExpiresAt: Now + NinetyDays,
            tokenExpiries: [Now - ThirtyDays - TimeSpan.FromDays(10), Now - ThirtyDays - OneDay, Now + TimeSpan.FromDays(14)]);
        var revoked = await SeedSessionAsync(
            sessionExpiresAt: Now + NinetyDays,
            revokedAt: Now - OneDay,
            tokenExpiries: [Now - ThirtyDays - OneDay, Now + TimeSpan.FromDays(3)]);

        // Both the refresh-token and the session job run; the live session stays, and so does every unexpired token.
        (await RunAsync(RefreshTokensCleanupJob.JobName)).Processed.ShouldBe(3);
        (await RunAsync(UserSessionsCleanupJob.JobName)).Processed.ShouldBe(0);

        (await SessionIdsAsync()).ShouldBe([live.SessionId, revoked.SessionId], ignoreOrder: true);
        (await TokenIdsAsync()).ShouldBe([live.TokenIds[2], revoked.TokenIds[1]], ignoreOrder: true);
    }

    [Fact]
    public async Task A_used_refresh_token_inside_its_lifetime_stays()
    {
        var session = await SeedSessionAsync(sessionExpiresAt: Now + NinetyDays, tokenExpiries: [Now + TimeSpan.FromDays(10), Now + TimeSpan.FromDays(14)]);
        Factory.Time.Advance(TimeSpan.FromDays(5));

        await RunAllAsync();

        (await UsedTokenIdsAsync()).ShouldBe([session.TokenIds[0]]);
        (await TokenIdsAsync()).ShouldBe(session.TokenIds, ignoreOrder: true);
    }

    [Fact]
    public async Task Sessions_expired_or_revoked_more_than_ninety_days_ago_go_with_their_tokens_and_the_boundary_stays()
    {
        var revokedExactly = await SeedSessionAsync(Now + OneDay, revokedAt: Now - NinetyDays, tokenExpiries: [Now + OneDay]);
        var revokedJustOver = await SeedSessionAsync(Now + OneDay, revokedAt: Now - NinetyDays - OneSecond, tokenExpiries: [Now + OneDay, Now + OneDay]);
        var expiredExactly = await SeedSessionAsync(Now - NinetyDays, tokenExpiries: [Now + OneDay]);
        var expiredJustOver = await SeedSessionAsync(Now - NinetyDays - OneSecond, tokenExpiries: [Now + OneDay, Now + OneDay, Now + OneDay]);

        var result = await RunAsync(UserSessionsCleanupJob.JobName);

        // The result counts sessions, not the tokens that went by cascade.
        result.Processed.ShouldBe(2);
        (await SessionIdsAsync()).ShouldBe([revokedExactly.SessionId, expiredExactly.SessionId], ignoreOrder: true);
        (await TokenIdsAsync()).ShouldBe([.. revokedExactly.TokenIds, .. expiredExactly.TokenIds], ignoreOrder: true);
    }

    [Fact]
    public async Task A_revoked_session_goes_after_ninety_days_with_its_tokens()
    {
        var session = await SeedSessionAsync(Now + NinetyDays, revokedAt: Now, tokenExpiries: [Now + OneDay, Now + TimeSpan.FromDays(14)]);
        var other = await SeedSessionAsync(Now + NinetyDays, tokenExpiries: [Now + OneDay]);

        // Still inside the retention on day 90, gone a second later.
        Factory.Time.Advance(NinetyDays);
        (await RunAsync(UserSessionsCleanupJob.JobName)).Processed.ShouldBe(0);
        (await SessionIdsAsync()).ShouldContain(session.SessionId);

        Factory.Time.Advance(OneSecond);
        (await RunAsync(UserSessionsCleanupJob.JobName)).Processed.ShouldBe(1);

        (await SessionIdsAsync()).ShouldNotContain(session.SessionId);
        (await TokenIdsAsync()).ShouldBe(other.TokenIds);
    }

    [Fact]
    public async Task A_session_expired_long_ago_but_never_revoked_goes()
    {
        var session = await SeedSessionAsync(Now - TimeSpan.FromDays(400), tokenExpiries: [Now - TimeSpan.FromDays(400)]);
        session.SessionId.ShouldNotBe(Guid.Empty);

        (await RunAsync(UserSessionsCleanupJob.JobName)).Processed.ShouldBe(1);

        (await SessionIdsAsync()).ShouldBeEmpty();
        (await TokenIdsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_backlog_of_sessions_drains_over_several_runs_with_their_tokens()
    {
        for (var index = 0; index < 5; index++)
        {
            await SeedSessionAsync(Now - NinetyDays - OneDay, tokenExpiries: [Now + OneDay, Now + OneDay]);
        }

        (await RunAsync(UserSessionsCleanupJob.JobName, batchSize: 2, maxBatchesPerRun: 1)).Processed.ShouldBe(2);
        (await SessionIdsAsync()).Count.ShouldBe(3);
        (await TokenIdsAsync()).Count.ShouldBe(6);
        (await RunAsync(UserSessionsCleanupJob.JobName, batchSize: 2, maxBatchesPerRun: 1)).Processed.ShouldBe(2);
        (await RunAsync(UserSessionsCleanupJob.JobName, batchSize: 2, maxBatchesPerRun: 1)).Processed.ShouldBe(1);

        (await SessionIdsAsync()).ShouldBeEmpty();
        (await TokenIdsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Mfa_challenges_expired_more_than_one_day_ago_go_and_a_challenge_expired_exactly_one_day_ago_stays()
    {
        var exactly = await SeedMfaChallengeAsync(Now - OneDay);
        var justOver = await SeedMfaChallengeAsync(Now - OneDay - OneSecond);
        var pending = await SeedMfaChallengeAsync(Now + TimeSpan.FromMinutes(5));

        var result = await RunAsync(MfaChallengesCleanupJob.JobName);

        result.Processed.ShouldBe(1);
        var remaining = await MfaChallengeIdsAsync();
        remaining.ShouldBe([exactly, pending], ignoreOrder: true);
        remaining.ShouldNotContain(justOver);
    }

    [Fact]
    public async Task Trusted_devices_expired_more_than_thirty_days_ago_go_and_a_device_expired_exactly_thirty_days_ago_stays()
    {
        var exactly = await SeedTrustedDeviceAsync(Now - ThirtyDays);
        var justOver = await SeedTrustedDeviceAsync(Now - ThirtyDays - OneSecond);
        var active = await SeedTrustedDeviceAsync(Now + OneDay);

        var result = await RunAsync(TrustedDevicesCleanupJob.JobName);

        result.Processed.ShouldBe(1);
        var remaining = await TrustedDeviceIdsAsync();
        remaining.ShouldBe([exactly, active], ignoreOrder: true);
        remaining.ShouldNotContain(justOver);
    }

    [Fact]
    public async Task A_revoked_trusted_device_with_a_far_expiry_goes_thirty_days_after_revocation()
    {
        var farFuture = Now + TimeSpan.FromDays(300);
        var revokedExactly = await SeedTrustedDeviceAsync(farFuture, revokedAt: Now - ThirtyDays);
        var revokedJustOver = await SeedTrustedDeviceAsync(farFuture, revokedAt: Now - ThirtyDays - OneSecond);
        var revokedRecently = await SeedTrustedDeviceAsync(farFuture, revokedAt: Now - TimeSpan.FromDays(2));
        var neverRevoked = await SeedTrustedDeviceAsync(farFuture);

        var result = await RunAsync(TrustedDevicesCleanupJob.JobName);

        result.Processed.ShouldBe(1);
        var remaining = await TrustedDeviceIdsAsync();
        remaining.ShouldBe([revokedExactly, revokedRecently, neverRevoked], ignoreOrder: true);
        remaining.ShouldNotContain(revokedJustOver);
    }

    [Fact]
    public async Task Audit_logs_older_than_the_retention_go_and_a_row_exactly_at_the_cutoff_stays()
    {
        var exactly = await SeedAuditLogAsync(Now - OneYear);
        var justOver = await SeedAuditLogAsync(Now - OneYear - OneSecond);
        var recent = await SeedAuditLogAsync(Now - OneDay);

        var result = await RunAsync(AuditLogsCleanupJob.JobName, new AuthRetentionOptions { AuditLogs = OneYear });

        result.Processed.ShouldBe(1);
        var remaining = await AuditLogIdsAsync();
        remaining.ShouldBe([exactly, recent], ignoreOrder: true);
        remaining.ShouldNotContain(justOver);
    }

    [Fact]
    public async Task Audit_logs_are_kept_by_default()
    {
        await SeedAuditLogAsync(Now - TimeSpan.FromDays(3650));
        await SeedAuditLogAsync(Now - OneDay);
        var runner = Factory.Services.GetRequiredService<RecurringJobRunner>();

        // The host's own configuration: the job is registered and runs, and deletes nothing.
        (await runner.RunDueAsync(name => name == AuditLogsCleanupJob.JobName, Ct)).ShouldBe(1);

        (await AuditLogIdsAsync()).Count.ShouldBe(2);
    }

    [Theory]
    [MemberData(nameof(AllJobNamesWithOffValues))]
    public async Task Retention_zero_or_null_disables_every_auth_job(string jobName, bool zero)
    {
        var longAgo = Now - TimeSpan.FromDays(4000);
        await SeedVerificationCodeAsync(longAgo);
        await SeedSessionAsync(longAgo, tokenExpiries: [longAgo]);
        await SeedSessionAsync(Now + OneDay, revokedAt: longAgo, tokenExpiries: [longAgo]);
        await SeedMfaChallengeAsync(longAgo);
        await SeedTrustedDeviceAsync(longAgo, revokedAt: longAgo);
        await SeedAuditLogAsync(longAgo);
        TimeSpan? off = zero ? TimeSpan.Zero : null;
        var retention = new AuthRetentionOptions
        {
            VerificationCodes = off,
            RefreshTokens = off,
            Sessions = off,
            MfaChallenges = off,
            TrustedDevices = off,
            AuditLogs = off,
        };

        var result = await RunAsync(jobName, retention);

        result.Processed.ShouldBe(0);
        (await VerificationCodeIdsAsync()).Count.ShouldBe(1);
        (await SessionIdsAsync()).Count.ShouldBe(2);
        (await TokenIdsAsync()).Count.ShouldBe(2);
        (await MfaChallengeIdsAsync()).Count.ShouldBe(1);
        (await TrustedDeviceIdsAsync()).Count.ShouldBe(1);
        (await AuditLogIdsAsync()).Count.ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(AllJobNames))]
    public async Task A_job_constructor_opens_no_connection(string jobName)
    {
        // The scheduler resolves every job once at host start to read its name and interval.
        await using var scope = Factory.Services.CreateAsyncScope();

        var job = CreateJob(jobName, scope, new AuthRetentionOptions(), batchSize: 1000, maxBatchesPerRun: 100);
        _ = job.Name;
        _ = job.DefaultInterval;

        scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.GetDbConnection().State.ShouldBe(ConnectionState.Closed);
    }

    [Fact]
    public async Task Refresh_token_reuse_detection_still_works_for_a_token_inside_its_lifetime()
    {
        await CreateUserAsync(Email);
        var login = await LoginAsync();
        using (var first = await RefreshAsync(login.RefreshToken))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // A day later, with every cleanup job run: the rotated token is used but inside its lifetime, so it is kept.
        Factory.Time.Advance(OneDay);
        await RunAllAsync();
        (await UsedTokenIdsAsync()).Count.ShouldBe(1);

        using var reuse = await RefreshAsync(login.RefreshToken);

        reuse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await reuse.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("auth.refresh_token_reused");
        var session = await QueryAsync(context => context.Set<UserSession>().SingleAsync(Ct));
        session.RevokedAt.ShouldNotBeNull();
        session.RevokedReason.ShouldBe(SessionRevokedReason.TokenReuse);

        // And the cleanup does not undo it: the revoked session and its tokens are well inside the retention.
        Factory.Time.Advance(OneDay + TimeSpan.FromHours(1));
        await RunAllAsync();
        (await SessionIdsAsync()).ShouldBe([login.SessionId]);
        (await TokenIdsAsync()).Count.ShouldBe(2);
        using var again = await RefreshAsync(login.RefreshToken);
        again.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Cleanup_predicates_use_their_indexes()
    {
        // A table of mostly live rows with an old tail of about a tenth of a per cent, as a real one is: the cutoff selects few rows, so a seek on
        // the index is the plan. Set-based, so the tables are big enough that scanning them costs more than the seeks and key lookups.
        var userId = await UserIdAsync();
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO [auth].[UserSessions] ([Id], [UserId], [AuthMethods], [DeviceName], [SecurityStamp], [CreatedAt], [LastSeenAt], [AuthenticatedAt], [ExpiresAt], [RevokedAt])
                SELECT NEWID(), {0}, 'pwd', 'Plans', 'stamp', {1}, {1}, {1},
                    CASE WHEN n % 1000 = 0 THEN DATEADD(day, -120, {1}) ELSE DATEADD(day, 1 + n % 80, {1}) END,
                    CASE WHEN n % 1000 = 1 THEN DATEADD(day, -100, {1}) END
                FROM (SELECT TOP (200000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b) AS numbers;

                INSERT INTO [auth].[RefreshTokens] ([Id], [SessionId], [TokenHash], [CreatedAt], [ExpiresAt])
                SELECT NEWID(), [Id], CRYPT_GEN_RANDOM(32), {1},
                    CASE WHEN ROW_NUMBER() OVER (ORDER BY [Id]) % 1000 = 2 THEN DATEADD(day, -40, {1}) ELSE DATEADD(day, 10, {1}) END
                FROM [auth].[UserSessions];
                """,
                [userId, Now.UtcDateTime],
                Ct);
        }

        await using var planScope = Factory.Services.CreateAsyncScope();
        var connection = planScope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.GetDbConnection();

        var tokenPlan = await PlanOfAsync(RefreshTokensCleanupJob.JobName, planScope, connection);
        var sessionPlan = await PlanOfAsync(UserSessionsCleanupJob.JobName, planScope, connection);

        tokenPlan.ShouldContain("IX_RefreshTokens_ExpiresAt");
        SqlExecutionPlan.ClusteredIndexScansOf(tokenPlan, "[RefreshTokens]").ShouldBeEmpty("the cleanup must not scan the refresh token table");
        sessionPlan.ShouldContain("IX_UserSessions_ExpiresAt");
        sessionPlan.ShouldContain("IX_UserSessions_RevokedAt");
        SqlExecutionPlan.ClusteredIndexScansOf(sessionPlan, "[UserSessions]").ShouldBeEmpty("the cleanup must not scan the session table");
    }

    /// <summary>
    /// The plan of the job's own statement, compiled but not run. The plan helper declares <c>@Batch</c> and <c>@Cutoff</c> as local variables,
    /// which SQL Server estimates with a flat guess (a third of the rows per comparison); the job binds them as parameters, whose values it
    /// sniffs. The hint gives the compile the values the job would bind, so the plan is the one the job gets.
    /// </summary>
    private async Task<string> PlanOfAsync(string jobName, AsyncServiceScope scope, System.Data.Common.DbConnection connection)
    {
        var statement = CreateJob(jobName, scope, new AuthRetentionOptions(), batchSize: 1000, maxBatchesPerRun: 100).DeleteStatement;
        var cutoff = (Now - NinetyDays).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var hinted = statement.TrimEnd().TrimEnd(';') + $" OPTION (OPTIMIZE FOR (@Batch = 1000, @Cutoff = '{cutoff}'));";

        return await SqlExecutionPlan.OfDeleteAsync(connection, hinted, Ct);
    }

    private static byte[] NewHash() => RandomNumberGenerator.GetBytes(RefreshTokenConfiguration.TokenHashLength);

    private BatchedCleanupJob CreateJob(string name, AsyncServiceScope scope, AuthRetentionOptions retention, int batchSize, int maxBatchesPerRun)
    {
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var jobs = Options.Create(new JobsOptions { MaxBatchesPerRun = maxBatchesPerRun });
        var batching = Options.Create(new RetentionOptions { BatchSize = batchSize });
        var auth = Options.Create(retention);

        return name switch
        {
            VerificationCodesCleanupJob.JobName => new VerificationCodesCleanupJob(context, Factory.Time, jobs, batching, auth),
            RefreshTokensCleanupJob.JobName => new RefreshTokensCleanupJob(context, Factory.Time, jobs, batching, auth),
            UserSessionsCleanupJob.JobName => new UserSessionsCleanupJob(context, Factory.Time, jobs, batching, auth),
            MfaChallengesCleanupJob.JobName => new MfaChallengesCleanupJob(context, Factory.Time, jobs, batching, auth),
            TrustedDevicesCleanupJob.JobName => new TrustedDevicesCleanupJob(context, Factory.Time, jobs, batching, auth),
            AuditLogsCleanupJob.JobName => new AuditLogsCleanupJob(context, Factory.Time, jobs, batching, auth),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not an Auth cleanup job."),
        };
    }

    /// <summary>Runs the named job once with the default retentions (the audit log is kept).</summary>
    private Task<JobResult> RunAsync(string jobName, int batchSize = 1000, int maxBatchesPerRun = 100)
        => RunAsync(jobName, new AuthRetentionOptions(), batchSize, maxBatchesPerRun);

    private async Task<JobResult> RunAsync(string jobName, AuthRetentionOptions retention, int batchSize = 1000, int maxBatchesPerRun = 100)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await CreateJob(jobName, scope, retention, batchSize, maxBatchesPerRun).RunAsync(Ct);
    }

    /// <summary>Runs the six Auth cleanup jobs as the scheduler does, with the host's own configuration.</summary>
    private async Task RunAllAsync()
    {
        var runner = Factory.Services.GetRequiredService<RecurringJobRunner>();
        (await runner.RunDueAsync(name => JobNames.Contains(name, StringComparer.Ordinal), Ct)).ShouldBe(JobNames.Length);
    }

    private async Task<Guid> UserIdAsync()
    {
        if (_userId is { } existing)
        {
            return existing;
        }

        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = User.Register("seed@example.com", "Test user", "en", "hash", Now).Value;
        context.Add(user);
        await context.SaveChangesAsync(Ct);
        _userId = user.Id;
        return user.Id;
    }

    private async Task<Guid> SeedVerificationCodeAsync(DateTimeOffset expiresAt)
    {
        var userId = await UserIdAsync();
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var code = VerificationCode.Issue(
            userId,
            VerificationPurpose.EmailVerify,
            VerificationTrigger.SelfService,
            "seed@example.com",
            NewHash(),
            "protected-token",
            OneDay,
            createdIp: null,
            Now);
        context.Add(code);
        await context.SaveChangesAsync(Ct);

        await context.Set<VerificationCode>().Where(candidate => candidate.Id == code.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(candidate => candidate.ExpiresAt, expiresAt), Ct);
        return code.Id;
    }

    private async Task<Guid> SeedMfaChallengeAsync(DateTimeOffset expiresAt)
    {
        var userId = await UserIdAsync();
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var challenge = MfaChallenge.Issue(userId, NewHash(), "stamp", "Seed device", TimeSpan.FromMinutes(5), Now);
        context.Add(challenge);
        await context.SaveChangesAsync(Ct);

        await context.Set<MfaChallenge>().Where(candidate => candidate.Id == challenge.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(candidate => candidate.ExpiresAt, expiresAt), Ct);
        return challenge.Id;
    }

    private async Task<Guid> SeedTrustedDeviceAsync(DateTimeOffset expiresAt, DateTimeOffset? revokedAt = null)
    {
        var userId = await UserIdAsync();
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var device = TrustedDevice.Trust(userId, NewHash(), "Seed device", userAgent: null, "stamp", ThirtyDays, Now);
        context.Add(device);
        await context.SaveChangesAsync(Ct);

        await context.Set<TrustedDevice>().Where(candidate => candidate.Id == device.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(candidate => candidate.ExpiresAt, expiresAt).SetProperty(candidate => candidate.RevokedAt, revokedAt), Ct);
        return device.Id;
    }

    private async Task<long> SeedAuditLogAsync(DateTimeOffset occurredAt)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var entry = AuthAuditLog.Create(AuthAuditEvents.LoginSucceeded, succeeded: true, occurredAt);
        context.Add(entry);
        await context.SaveChangesAsync(Ct);
        return entry.Id;
    }

    /// <summary>
    /// A session of one user with a chain of refresh tokens, one per <paramref name="tokenExpiries"/> entry in order (each but the last
    /// rotated, so used), whose expiry, revocation and token expiries are set to exactly the given instants.
    /// </summary>
    private async Task<SeededSession> SeedSessionAsync(DateTimeOffset sessionExpiresAt, DateTimeOffset? revokedAt = null, DateTimeOffset[]? tokenExpiries = null)
    {
        tokenExpiries ??= [Now + OneDay];
        var userId = await UserIdAsync();
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        var hash = NewHash();
        var (session, _) = UserSession.Start(userId, "pwd", "Seed device", userAgent: null, ipAddress: null, "stamp", hash, SevenDays, NinetyDays, Now);
        for (var index = 1; index < tokenExpiries.Length; index++)
        {
            var next = NewHash();
            session.Rotate(hash, next, "stamp", SevenDays, Now).IsSuccess.ShouldBeTrue();
            hash = next;
        }

        if (revokedAt is not null)
        {
            session.Revoke(SessionRevokedReason.Logout, Now);
        }

        context.Add(session);
        await context.SaveChangesAsync(Ct);

        // The domain fixes these from "now" and the lifetimes; the tests want exact instants either side of a cutoff.
        var tokenIds = session.RefreshTokens.Select(token => token.Id).ToList();
        await context.Set<UserSession>().Where(candidate => candidate.Id == session.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(candidate => candidate.ExpiresAt, sessionExpiresAt).SetProperty(candidate => candidate.RevokedAt, revokedAt), Ct);
        for (var index = 0; index < tokenIds.Count; index++)
        {
            var id = tokenIds[index];
            var expiresAt = tokenExpiries[index];
            await context.Set<RefreshToken>().Where(candidate => candidate.Id == id)
                .ExecuteUpdateAsync(set => set.SetProperty(candidate => candidate.ExpiresAt, expiresAt), Ct);
        }

        return new SeededSession(session.Id, tokenIds);
    }

    private Task<List<Guid>> VerificationCodeIdsAsync() => QueryAsync(context => context.Set<VerificationCode>().Select(row => row.Id).ToListAsync(Ct));

    private Task<List<Guid>> SessionIdsAsync() => QueryAsync(context => context.Set<UserSession>().Select(row => row.Id).ToListAsync(Ct));

    private Task<List<Guid>> TokenIdsAsync() => QueryAsync(context => context.Set<RefreshToken>().Select(row => row.Id).ToListAsync(Ct));

    private Task<List<Guid>> UsedTokenIdsAsync()
        => QueryAsync(context => context.Set<RefreshToken>().Where(row => row.UsedAt != null).Select(row => row.Id).ToListAsync(Ct));

    private Task<List<Guid>> MfaChallengeIdsAsync() => QueryAsync(context => context.Set<MfaChallenge>().Select(row => row.Id).ToListAsync(Ct));

    private Task<List<Guid>> TrustedDeviceIdsAsync() => QueryAsync(context => context.Set<TrustedDevice>().Select(row => row.Id).ToListAsync(Ct));

    private Task<List<long>> AuditLogIdsAsync() => QueryAsync(context => context.Set<AuthAuditLog>().Select(row => row.Id).ToListAsync(Ct));

    private async Task<T> QueryAsync<T>(Func<AuthDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    private async Task<LoginTokens> LoginAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, LoginRoute)
        {
            Content = JsonContent.Create(new { email = Email, password = AuthTestHarness.Password }),
        };
        using var response = await Client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return new LoginTokens(body.GetProperty("refreshToken").GetString()!, body.GetProperty("sessionId").GetGuid());
    }

    private async Task<HttpResponseMessage> RefreshAsync(string refreshToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RefreshRoute) { Content = JsonContent.Create(new { refreshToken }) };
        return await Client.SendAsync(request, Ct);
    }

    private sealed record SeededSession(Guid SessionId, List<Guid> TokenIds);

    private sealed record LoginTokens(string RefreshToken, Guid SessionId);
}
