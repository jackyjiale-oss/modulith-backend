using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.TrustedDevices;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

public sealed class MfaPersistenceTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const int SqlUniqueIndexViolation = 2601;
    private const int MaxAttempts = 5;
    private const string PreviousMigration = "AddRoleAndAuditLogListIndexes";
    private const string MfaMigration = "AddMultiFactorAuthentication";
    private const string Backfill = "UPDATE [auth].[UserSessions] SET [AuthenticatedAt] = [CreatedAt]";

    /// <summary>How many default constraints <c>auth.UserSessions.AuthenticatedAt</c> has, as text; the migration drops the one it adds.</summary>
    private const string AuthenticatedAtDefaultsSql = """
        SELECT CAST(COUNT(*) AS varchar(10))
        FROM sys.default_constraints dc
        JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
        WHERE dc.parent_object_id = OBJECT_ID('auth.UserSessions') AND c.name = 'AuthenticatedAt'
        """;

    private static readonly TimeSpan SlidingLifetime = TimeSpan.FromDays(14);
    private static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(90);
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TrustedDeviceLifetime = TimeSpan.FromDays(30);

    private readonly List<AsyncServiceScope> _scopes = [];

    private DateTimeOffset Now => Factory.Time.GetUtcNow();

    [Fact]
    public async Task Mfa_rows_round_trip()
    {
        var user = NewUser("alice@example.com");
        var other = NewUser("bob@example.com");
        var method = UserMfaMethod.StartTotp(user.Id, "Phone", "protected-secret", Now);
        var step = Totp.TimeStepAt(Now);
        method.Confirm(step, Now).IsSuccess.ShouldBeTrue();
        var pending = UserMfaMethod.StartTotp(other.Id, "Tablet", "other-protected-secret", Now);
        var unused = RecoveryCode.Create(user.Id, Hash(1), Now);
        var used = RecoveryCode.Create(user.Id, Hash(2), Now);
        var challenge = MfaChallenge.Issue(user.Id, Hash(3), user.SecurityStamp, "Firefox on Windows", ChallengeLifetime, Now);
        var device = TrustedDevice.Trust(user.Id, Hash(4), "Firefox on Windows", "Mozilla/5.0", user.SecurityStamp, TrustedDeviceLifetime, Now);
        var (session, _) = UserSession.Start(
            user.Id, "pwd", "Firefox on Windows", null, null, user.SecurityStamp, Hash(5), SlidingLifetime, AbsoluteLifetime, Now);
        await SaveAsync(user, other, method, pending, unused, used, challenge, device, session);
        (await NewScope().GetRequiredService<IRecoveryCodeRepository>().TryUseAsync(used.Id, Now, Ct)).ShouldBeTrue();

        var methods = NewScope().GetRequiredService<IMfaMethodRepository>();
        var saved = (await methods.GetTotpAsync(user.Id, Ct)).ShouldNotBeNull();
        saved.Id.ShouldBe(method.Id);
        saved.UserId.ShouldBe(user.Id);
        saved.Type.ShouldBe(MfaMethodType.Totp);
        saved.Label.ShouldBe("Phone");
        saved.SecretEncrypted.ShouldBe("protected-secret");
        saved.CreatedAt.ShouldBe(Now);
        saved.ConfirmedAt.ShouldBe(Now);
        saved.LastUsedAt.ShouldBe(Now);
        saved.LastUsedTimeStep.ShouldBe(step);
        saved.IsConfirmed.ShouldBeTrue();
        (await methods.GetForUserAsync(method.Id, user.Id, Ct)).ShouldNotBeNull().Id.ShouldBe(method.Id);
        (await methods.GetForUserAsync(method.Id, other.Id, Ct)).ShouldBeNull();
        (await methods.HasConfirmedAsync(user.Id, Ct)).ShouldBeTrue();
        (await methods.HasConfirmedAsync(other.Id, Ct)).ShouldBeFalse();
        (await methods.GetTotpAsync(other.Id, Ct)).ShouldNotBeNull().IsConfirmed.ShouldBeFalse();

        var codes = NewScope().GetRequiredService<IRecoveryCodeRepository>();
        var listed = await codes.ListByUserAsync(user.Id, Ct);
        listed.Select(code => code.Id).ShouldBe([unused.Id, used.Id], ignoreOrder: true);
        listed.Single(code => code.Id == used.Id).UsedAt.ShouldBe(Now);
        (await codes.FindUnusedAsync(user.Id, Hash(1), Ct)).ShouldNotBeNull().CodeHash.ShouldBe(Hash(1));
        (await codes.FindUnusedAsync(user.Id, Hash(2), Ct)).ShouldBeNull();
        (await codes.FindUnusedAsync(other.Id, Hash(1), Ct)).ShouldBeNull();
        (await codes.CountUnusedAsync(user.Id, Ct)).ShouldBe(1);

        var savedChallenge = (await NewScope().GetRequiredService<IMfaChallengeRepository>().GetByTokenHashAsync(Hash(3), Ct)).ShouldNotBeNull();
        savedChallenge.Id.ShouldBe(challenge.Id);
        savedChallenge.UserId.ShouldBe(user.Id);
        savedChallenge.SecurityStamp.ShouldBe(user.SecurityStamp);
        savedChallenge.DeviceName.ShouldBe("Firefox on Windows");
        savedChallenge.CreatedAt.ShouldBe(Now);
        savedChallenge.ExpiresAt.ShouldBe(Now + ChallengeLifetime);
        savedChallenge.ConsumedAt.ShouldBeNull();
        savedChallenge.FailedAttempts.ShouldBe(0);
        (await NewScope().GetRequiredService<IMfaChallengeRepository>().GetByTokenHashAsync(Hash(99), Ct)).ShouldBeNull();

        var devices = NewScope().GetRequiredService<ITrustedDeviceRepository>();
        var savedDevice = (await devices.GetByTokenHashAsync(Hash(4), Ct)).ShouldNotBeNull();
        savedDevice.Id.ShouldBe(device.Id);
        savedDevice.DeviceName.ShouldBe("Firefox on Windows");
        savedDevice.UserAgent.ShouldBe("Mozilla/5.0");
        savedDevice.SecurityStamp.ShouldBe(user.SecurityStamp);
        savedDevice.ExpiresAt.ShouldBe(Now + TrustedDeviceLifetime);
        savedDevice.LastUsedAt.ShouldBeNull();
        savedDevice.RevokedAt.ShouldBeNull();
        (await devices.GetForUserAsync(device.Id, user.Id, Ct)).ShouldNotBeNull();
        (await devices.GetForUserAsync(device.Id, other.Id, Ct)).ShouldBeNull();

        // AuthenticatedAt is a column now: set at the start, moved by a step-up.
        (await NewScope().GetRequiredService<ISessionRepository>().GetByIdAsync(session.Id, Ct)).ShouldNotBeNull().AuthenticatedAt.ShouldBe(Now);
        var startedAt = Now;
        Factory.Time.Advance(TimeSpan.FromMinutes(3));
        var stepUpScope = NewScope();
        var toStepUp = (await stepUpScope.GetRequiredService<ISessionRepository>().GetByIdAsync(session.Id, Ct)).ShouldNotBeNull();
        toStepUp.StepUp("otp", Now);
        await stepUpScope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        var steppedUp = (await NewScope().GetRequiredService<ISessionRepository>().GetByIdAsync(session.Id, Ct)).ShouldNotBeNull();
        steppedUp.AuthenticatedAt.ShouldBe(Now);
        steppedUp.AuthMethods.ShouldBe("otp");
        steppedUp.CreatedAt.ShouldBe(startedAt);

        // The tracked instances change through the change tracker: revoke the device, remove a code and the method.
        var changeScope = NewScope();
        var trackedDevice = (await changeScope.GetRequiredService<ITrustedDeviceRepository>().GetForUserAsync(device.Id, user.Id, Ct)).ShouldNotBeNull();
        trackedDevice.MarkUsed(Now);
        trackedDevice.Revoke(Now);
        var trackedCodes = await changeScope.GetRequiredService<IRecoveryCodeRepository>().ListByUserAsync(user.Id, Ct);
        changeScope.GetRequiredService<IRecoveryCodeRepository>().Remove(trackedCodes.Single(code => code.Id == used.Id));
        var trackedMethod = (await changeScope.GetRequiredService<IMfaMethodRepository>().GetForUserAsync(method.Id, user.Id, Ct)).ShouldNotBeNull();
        changeScope.GetRequiredService<IMfaMethodRepository>().Remove(trackedMethod);
        await changeScope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);

        var changed = (await NewScope().GetRequiredService<ITrustedDeviceRepository>().GetForUserAsync(device.Id, user.Id, Ct)).ShouldNotBeNull();
        changed.LastUsedAt.ShouldBe(Now);
        changed.RevokedAt.ShouldBe(Now);
        (await NewScope().GetRequiredService<IRecoveryCodeRepository>().ListByUserAsync(user.Id, Ct)).ShouldHaveSingleItem().Id.ShouldBe(unused.Id);
        (await NewScope().GetRequiredService<IMfaMethodRepository>().GetTotpAsync(user.Id, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Second_totp_method_for_one_user_violates_the_unique_index()
    {
        var user = NewUser("alice@example.com");
        await SaveAsync(user, UserMfaMethod.StartTotp(user.Id, "Phone", "protected-1", Now));

        // Two setups at once both found no method; the unique index lets one insert win, and the name is the one handlers rely on.
        var scope = NewScope();
        scope.GetRequiredService<IMfaMethodRepository>().Add(UserMfaMethod.StartTotp(user.Id, "Tablet", "protected-2", Now));
        (await scope.GetRequiredService<IUnitOfWork>().SaveChangesUnlessDuplicateAsync(UniqueIndexNames.MfaMethodPerUserAndType, Ct)).ShouldBeFalse();

        var exception = await Should.ThrowAsync<DbUpdateException>(() => SaveAsync(UserMfaMethod.StartTotp(user.Id, "Tablet", "protected-2", Now)));
        exception.InnerException.ShouldBeOfType<SqlException>().Number.ShouldBe(SqlUniqueIndexViolation);
        (await NewScope().GetRequiredService<IMfaMethodRepository>().GetTotpAsync(user.Id, Ct)).ShouldNotBeNull().Label.ShouldBe("Phone");
    }

    [Fact]
    public async Task Confirm_of_a_secret_a_restart_replaced_meanwhile_is_a_concurrency_conflict()
    {
        var user = NewUser("alice@example.com");
        await SaveAsync(user, UserMfaMethod.StartTotp(user.Id, "Phone", "protected-a", Now));
        var (restartScope, restarting) = await LoadTotpAsync(user);
        var (confirmScope, confirming) = await LoadTotpAsync(user);

        // The confirm verified its code against secret A; a setup restart commits secret B first.
        restarting.RestartSetup("Tablet", "protected-b", Now);
        await restartScope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        confirming.Confirm(Totp.TimeStepAt(Now), Now).IsSuccess.ShouldBeTrue();

        // The UPDATE also requires the secret it loaded, so a secret nobody verified is never confirmed.
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => confirmScope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct));

        var stored = (await NewScope().GetRequiredService<IMfaMethodRepository>().GetTotpAsync(user.Id, Ct)).ShouldNotBeNull();
        stored.SecretEncrypted.ShouldBe("protected-b");
        stored.IsConfirmed.ShouldBeFalse();
        stored.LastUsedTimeStep.ShouldBeNull();
    }

    [Fact]
    public async Task Restart_of_a_method_a_confirm_turned_on_meanwhile_is_a_concurrency_conflict()
    {
        var user = NewUser("alice@example.com");
        await SaveAsync(user, UserMfaMethod.StartTotp(user.Id, "Phone", "protected-a", Now));
        var (confirmScope, confirming) = await LoadTotpAsync(user);
        var (restartScope, restarting) = await LoadTotpAsync(user);

        // The setup loaded the method while it was pending, so the domain guard of RestartSetup cannot see the confirm that commits first.
        confirming.Confirm(Totp.TimeStepAt(Now), Now).IsSuccess.ShouldBeTrue();
        await confirmScope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        restarting.RestartSetup("Tablet", "protected-b", Now);

        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => restartScope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct));

        var stored = (await NewScope().GetRequiredService<IMfaMethodRepository>().GetTotpAsync(user.Id, Ct)).ShouldNotBeNull();
        stored.SecretEncrypted.ShouldBe("protected-a");
        stored.Label.ShouldBe("Phone");
        stored.IsConfirmed.ShouldBeTrue();
    }

    [Fact]
    public async Task Try_claim_time_step_succeeds_for_exactly_one_of_two_concurrent_callers()
    {
        var user = NewUser("alice@example.com");
        var method = ConfirmedMethod(user, out var step);
        await SaveAsync(user, method);
        Factory.Time.Advance(TimeSpan.FromSeconds(30));

        // A double tap: two verifies of the same code at once. The database decides; exactly one wins and neither throws.
        var claims = await Task.WhenAll(
            NewScope().GetRequiredService<IMfaMethodRepository>().TryClaimTimeStepAsync(method.Id, step + 1, Now, Ct),
            NewScope().GetRequiredService<IMfaMethodRepository>().TryClaimTimeStepAsync(method.Id, step + 1, Now, Ct));

        claims.Count(claimed => claimed).ShouldBe(1);
        var stored = (await NewScope().GetRequiredService<IMfaMethodRepository>().GetTotpAsync(user.Id, Ct)).ShouldNotBeNull();
        stored.LastUsedTimeStep.ShouldBe(step + 1);
        stored.LastUsedAt.ShouldBe(Now);
    }

    [Fact]
    public async Task Try_claim_time_step_refuses_an_equal_or_lower_step_and_a_pending_method()
    {
        var user = NewUser("alice@example.com");
        var other = NewUser("bob@example.com");
        var enrolledUser = NewUser("carol@example.com");
        var method = ConfirmedMethod(user, out var step);
        var pending = UserMfaMethod.StartTotp(other.Id, "Tablet", "protected", Now);
        await SaveAsync(user, other, enrolledUser, method, pending);
        var methods = NewScope().GetRequiredService<IMfaMethodRepository>();

        (await methods.TryClaimTimeStepAsync(method.Id, step, Now, Ct)).ShouldBeFalse();
        (await methods.TryClaimTimeStepAsync(method.Id, step - 1, Now, Ct)).ShouldBeFalse();
        (await methods.TryClaimTimeStepAsync(method.Id, step + 1, Now, Ct)).ShouldBeTrue();
        (await methods.TryClaimTimeStepAsync(method.Id, step + 1, Now, Ct)).ShouldBeFalse();
        (await methods.TryClaimTimeStepAsync(method.Id, step, Now, Ct)).ShouldBeFalse();
        (await methods.TryClaimTimeStepAsync(method.Id, step + 3, Now, Ct)).ShouldBeTrue();

        // A pending method protects nothing, so no code of it can be claimed; neither can a method that does not exist.
        (await methods.TryClaimTimeStepAsync(pending.Id, step + 1, Now, Ct)).ShouldBeFalse();
        (await methods.TryClaimTimeStepAsync(Guid.NewGuid(), step + 1, Now, Ct)).ShouldBeFalse();
        var storedPending = (await NewScope().GetRequiredService<IMfaMethodRepository>().GetTotpAsync(other.Id, Ct)).ShouldNotBeNull();
        storedPending.LastUsedTimeStep.ShouldBeNull();
        storedPending.LastUsedAt.ShouldBeNull();

        // A confirmed method that has used no step yet (the harness enrollment) accepts the current step once.
        var enrolled = await EnrollTotpAsync(enrolledUser.Id);
        var current = Totp.TimeStepAt(Now);
        (await methods.TryClaimTimeStepAsync(enrolled.MethodId, current, Now, Ct)).ShouldBeTrue();
        (await methods.TryClaimTimeStepAsync(enrolled.MethodId, current, Now, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Try_use_recovery_code_succeeds_once()
    {
        var user = NewUser("alice@example.com");
        var code = RecoveryCode.Create(user.Id, Hash(1), Now);
        var spare = RecoveryCode.Create(user.Id, Hash(2), Now);
        await SaveAsync(user, code, spare);
        Factory.Time.Advance(TimeSpan.FromSeconds(5));

        var uses = await Task.WhenAll(
            NewScope().GetRequiredService<IRecoveryCodeRepository>().TryUseAsync(code.Id, Now, Ct),
            NewScope().GetRequiredService<IRecoveryCodeRepository>().TryUseAsync(code.Id, Now, Ct));

        uses.Count(isUsed => isUsed).ShouldBe(1);
        var codes = NewScope().GetRequiredService<IRecoveryCodeRepository>();
        (await codes.TryUseAsync(code.Id, Now, Ct)).ShouldBeFalse();
        (await codes.TryUseAsync(Guid.NewGuid(), Now, Ct)).ShouldBeFalse();
        (await codes.FindUnusedAsync(user.Id, Hash(1), Ct)).ShouldBeNull();
        (await codes.CountUnusedAsync(user.Id, Ct)).ShouldBe(1);
        (await codes.ListByUserAsync(user.Id, Ct)).Single(stored => stored.Id == code.Id).UsedAt.ShouldBe(Now);
    }

    [Fact]
    public async Task Failed_attempts_are_all_counted_when_concurrent()
    {
        var user = NewUser("alice@example.com");
        var challenge = NewChallenge(user, Hash(1));
        var consumed = NewChallenge(user, Hash(2));
        await SaveAsync(user, challenge, consumed);
        (await NewScope().GetRequiredService<IMfaChallengeRepository>().TryConsumeAsync(consumed.Id, Now, MaxAttempts, Ct)).ShouldBeTrue();

        // Ten wrong codes at once queue on the row lock; each is counted once and each caller sees its own new count.
        var counts = await Task.WhenAll(Enumerable.Range(0, 10).Select(
            _ => NewScope().GetRequiredService<IMfaChallengeRepository>().RecordFailedAttemptAsync(challenge.Id, Ct)));

        counts.ShouldBe([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], ignoreOrder: true);
        (await NewScope().GetRequiredService<IMfaChallengeRepository>().GetByTokenHashAsync(Hash(1), Ct)).ShouldNotBeNull().FailedAttempts.ShouldBe(10);

        // A consumed challenge and an unknown one count nothing.
        var challenges = NewScope().GetRequiredService<IMfaChallengeRepository>();
        (await challenges.RecordFailedAttemptAsync(consumed.Id, Ct)).ShouldBeNull();
        (await challenges.RecordFailedAttemptAsync(Guid.NewGuid(), Ct)).ShouldBeNull();
        (await NewScope().GetRequiredService<IMfaChallengeRepository>().GetByTokenHashAsync(Hash(2), Ct)).ShouldNotBeNull().FailedAttempts.ShouldBe(0);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("consumed")]
    [InlineData("exhausted")]
    [InlineData("valid until one tick before expiry")]
    public async Task Try_consume_challenge_follows_the_domain_rule(string state)
    {
        var user = NewUser("alice@example.com");
        var challenge = NewChallenge(user, Hash(1));
        await SaveAsync(user, challenge);
        var challenges = NewScope().GetRequiredService<IMfaChallengeRepository>();
        var at = state switch
        {
            "expired" => challenge.ExpiresAt,
            "valid until one tick before expiry" => challenge.ExpiresAt.AddMilliseconds(-1),
            _ => Now,
        };
        if (state == "consumed")
        {
            (await challenges.TryConsumeAsync(challenge.Id, Now, MaxAttempts, Ct)).ShouldBeTrue();
        }

        if (state == "exhausted")
        {
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                await challenges.RecordFailedAttemptAsync(challenge.Id, Ct);
            }
        }

        var loaded = (await NewScope().GetRequiredService<IMfaChallengeRepository>().GetByTokenHashAsync(Hash(1), Ct)).ShouldNotBeNull();

        var consumedNow = await NewScope().GetRequiredService<IMfaChallengeRepository>().TryConsumeAsync(challenge.Id, at, MaxAttempts, Ct);

        // The statement repeats MfaChallenge.CanUse (the stamp is the handler's check), so both must answer alike.
        consumedNow.ShouldBe(loaded.CanUse(user.SecurityStamp, at, MaxAttempts));
        consumedNow.ShouldBe(state == "valid until one tick before expiry");
        var stored = (await NewScope().GetRequiredService<IMfaChallengeRepository>().GetByTokenHashAsync(Hash(1), Ct)).ShouldNotBeNull();
        stored.ConsumedAt.ShouldBe(state switch
        {
            "consumed" => (DateTimeOffset?)Now,
            "valid until one tick before expiry" => at,
            _ => null,
        });
    }

    [Fact]
    public void Migration_backfills_AuthenticatedAt_from_CreatedAt()
    {
        var migrator = NewScope().GetRequiredService<AuthDbContext>().GetService<IMigrator>();

        var script = migrator.GenerateScript(fromMigration: PreviousMigration, toMigration: MfaMigration);

        // Add the column with a default (existing rows get a value), copy CreatedAt into it, then drop the default.
        var added = script.IndexOf("ADD [AuthenticatedAt] datetime2(3) NOT NULL", StringComparison.Ordinal);
        var backfilled = script.IndexOf(Backfill, StringComparison.Ordinal);
        var defaultDropped = script.IndexOf("DROP CONSTRAINT", backfilled < 0 ? 0 : backfilled, StringComparison.Ordinal);
        added.ShouldBeGreaterThanOrEqualTo(0, script);
        backfilled.ShouldBeGreaterThan(added, script);
        defaultDropped.ShouldBeGreaterThan(backfilled, script);
        script.ShouldContain("[TokenHash] varbinary(32) NOT NULL");
        script.ShouldContain("[CodeHash] varbinary(32) NOT NULL");
        script.ShouldContain("CREATE UNIQUE INDEX [IX_UserMfaMethods_UserId_Type]");
        script.ShouldContain("CREATE UNIQUE INDEX [IX_RecoveryCodes_UserId_CodeHash]");
        script.ShouldContain("CREATE UNIQUE INDEX [IX_MfaChallenges_TokenHash]");
        script.ShouldContain("CREATE UNIQUE INDEX [IX_TrustedDevices_TokenHash]");
    }

    [Fact]
    public async Task Upgrading_a_database_with_a_session_backfills_its_AuthenticatedAt()
    {
        // A database of its own on the same server, migrated to the schema before MFA and holding a session, then upgraded.
        var connectionString = new SqlConnectionStringBuilder(NewScope().GetRequiredService<AuthDbContext>().Database.GetConnectionString())
        {
            InitialCatalog = $"AuthUpgrade{Guid.NewGuid():N}",
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", AuthDbContext.Schema))
            .Options;
        await using var context = new AuthDbContext(options);
        try
        {
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration, Ct);
            var user = NewUser("alice@example.com");
            context.Add(user);
            await context.SaveChangesAsync(Ct);
            var sessionId = Guid.NewGuid();
            var createdAt = new DateTime(2025, 12, 24, 8, 30, 15, 123, DateTimeKind.Utc);
            await context.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO [auth].[UserSessions] ([Id], [UserId], [AuthMethods], [DeviceName], [SecurityStamp], [CreatedAt], [LastSeenAt], [ExpiresAt])
                VALUES ({sessionId}, {user.Id}, 'pwd', 'Old device', {user.SecurityStamp}, {createdAt}, {createdAt.AddDays(1)}, {createdAt.AddDays(90)})
                """,
                Ct);

            await migrator.MigrateAsync(cancellationToken: Ct);

            var session = await context.Set<UserSession>().AsNoTracking().SingleAsync(candidate => candidate.Id == sessionId, Ct);
            session.AuthenticatedAt.ShouldBe(new DateTimeOffset(createdAt));
            session.CreatedAt.ShouldBe(new DateTimeOffset(createdAt));
            (await StringsAsync(context, AuthenticatedAtDefaultsSql)).ShouldBe(["0"]);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Totp_secret_is_not_stored_in_plain_text()
    {
        var user = NewUser("alice@example.com");
        await SaveAsync(user);

        var enrollment = await EnrollTotpAsync(user.Id);

        // The whole row as text (every column), searched for each common spelling of the secret.
        var context = NewScope().GetRequiredService<AuthDbContext>();
        var rowText = await context.Database
            .SqlQueryRaw<string>("SELECT (SELECT * FROM [auth].[UserMfaMethods] FOR JSON PATH) AS [Value]")
            .SingleAsync(Ct);
        rowText.ShouldContain(enrollment.MethodId.ToString(), Case.Insensitive);
        rowText.ShouldNotContain(Base32.Encode(enrollment.Secret), Case.Insensitive);
        rowText.ShouldNotContain(Convert.ToHexString(enrollment.Secret), Case.Insensitive);
        rowText.ShouldNotContain(Convert.ToBase64String(enrollment.Secret).TrimEnd('='));

        // What is stored is the protected form, which only the dedicated protector reads back.
        var stored = (await NewScope().GetRequiredService<IMfaMethodRepository>().GetTotpAsync(user.Id, Ct)).ShouldNotBeNull();
        Factory.Services.GetRequiredService<IMfaSecretProtector>().Unprotect(stored.SecretEncrypted).ShouldBe(enrollment.Secret);
        stored.IsConfirmed.ShouldBeTrue();
        stored.LastUsedTimeStep.ShouldBeNull();
        enrollment.RecoveryCodes.Count.ShouldBe(RecoveryCodeFormat.Count);
        (await NewScope().GetRequiredService<IRecoveryCodeRepository>().CountUnusedAsync(user.Id, Ct)).ShouldBe(RecoveryCodeFormat.Count);
        var firstCode = RecoveryCodeFormat.Normalize(enrollment.RecoveryCodes[0]).ShouldNotBeNull();
        (await NewScope().GetRequiredService<IRecoveryCodeRepository>().FindUnusedAsync(user.Id, RecoveryCodeFormat.Hash(firstCode), Ct)).ShouldNotBeNull();
        (await NewScope().GetRequiredService<AuthDbContext>().Database
            .SqlQueryRaw<string>("SELECT (SELECT * FROM [auth].[RecoveryCodes] FOR JSON PATH) AS [Value]")
            .SingleAsync(Ct)).ShouldNotContain(firstCode, Case.Insensitive);
    }

    [Fact]
    public async Task Schema_matches_the_design()
    {
        var context = NewScope().GetRequiredService<AuthDbContext>();

        (await ColumnAsync(context, "UserMfaMethods", "Type")).ShouldBe("int NOT NULL");
        (await ColumnAsync(context, "UserMfaMethods", "Label")).ShouldBe("nvarchar(100) NOT NULL");
        (await ColumnAsync(context, "UserMfaMethods", "SecretEncrypted")).ShouldBe("nvarchar(512) NOT NULL");
        (await ColumnAsync(context, "UserMfaMethods", "LastUsedTimeStep")).ShouldBe("bigint NULL");
        (await ColumnAsync(context, "UserMfaMethods", "ConfirmedAt")).ShouldBe("datetime2(3) NULL");
        (await ColumnAsync(context, "RecoveryCodes", "CodeHash")).ShouldBe("varbinary(32) NOT NULL");
        (await ColumnAsync(context, "RecoveryCodes", "UsedAt")).ShouldBe("datetime2(3) NULL");
        (await ColumnAsync(context, "MfaChallenges", "TokenHash")).ShouldBe("varbinary(32) NOT NULL");
        (await ColumnAsync(context, "MfaChallenges", "SecurityStamp")).ShouldBe("nvarchar(64) NOT NULL");
        (await ColumnAsync(context, "MfaChallenges", "DeviceName")).ShouldBe("nvarchar(200) NOT NULL");
        (await ColumnAsync(context, "MfaChallenges", "FailedAttempts")).ShouldBe("int NOT NULL");
        (await ColumnAsync(context, "MfaChallenges", "ExpiresAt")).ShouldBe("datetime2(3) NOT NULL");
        (await ColumnAsync(context, "TrustedDevices", "TokenHash")).ShouldBe("varbinary(32) NOT NULL");
        (await ColumnAsync(context, "TrustedDevices", "DeviceName")).ShouldBe("nvarchar(200) NOT NULL");
        (await ColumnAsync(context, "TrustedDevices", "UserAgent")).ShouldBe("nvarchar(512) NULL");
        (await ColumnAsync(context, "TrustedDevices", "SecurityStamp")).ShouldBe("nvarchar(64) NOT NULL");
        (await ColumnAsync(context, "UserSessions", "AuthenticatedAt")).ShouldBe("datetime2(3) NOT NULL");
        (await StringsAsync(context, AuthenticatedAtDefaultsSql)).ShouldBe(["0"]);

        var indexes = await IndexesAsync(context, ["UserMfaMethods", "RecoveryCodes", "MfaChallenges", "TrustedDevices"]);
        indexes.ShouldBe(
            new Dictionary<string, string>
            {
                ["IX_UserMfaMethods_UserId_Type"] = "UNIQUE (UserId, Type)",
                ["IX_RecoveryCodes_UserId_CodeHash"] = "UNIQUE (UserId, CodeHash)",
                ["IX_MfaChallenges_TokenHash"] = "UNIQUE (TokenHash)",
                ["IX_MfaChallenges_UserId"] = "(UserId)",
                ["IX_TrustedDevices_TokenHash"] = "UNIQUE (TokenHash)",
                ["IX_TrustedDevices_UserId_CreatedAt_Id"] = "(UserId, CreatedAt, Id)",
            },
            ignoreOrder: true);

        // Every new table refers to its user by id only, and deleting a user row never takes them along (users are soft-deleted).
        var foreignKeys = await StringsAsync(
            context,
            """
            SELECT OBJECT_NAME(fk.parent_object_id) + ' -> ' + OBJECT_NAME(fk.referenced_object_id) + ' ' + fk.delete_referential_action_desc
            FROM sys.foreign_keys fk
            WHERE OBJECT_NAME(fk.parent_object_id) IN ('UserMfaMethods', 'RecoveryCodes', 'MfaChallenges', 'TrustedDevices')
              AND OBJECT_SCHEMA_NAME(fk.parent_object_id) = 'auth'
            """);
        foreignKeys.ShouldBe(
            [
                "UserMfaMethods -> Users NO_ACTION",
                "RecoveryCodes -> Users NO_ACTION",
                "MfaChallenges -> Users NO_ACTION",
                "TrustedDevices -> Users NO_ACTION",
            ],
            ignoreOrder: true);
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes)
        {
            await scope.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    private static byte[] Hash(int seed) => SHA256.HashData(BitConverter.GetBytes(seed));

    private static async Task<string> ColumnAsync(AuthDbContext context, string table, string column)
    {
        var rows = await StringsAsync(
            context,
            $"""
            SELECT CASE
                WHEN t.name = 'nvarchar' THEN 'nvarchar(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
                WHEN t.name = 'varbinary' THEN 'varbinary(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length AS varchar(10)) END + ')'
                WHEN t.name = 'datetime2' THEN 'datetime2(' + CAST(c.scale AS varchar(10)) + ')'
                ELSE t.name END + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END
            FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID('auth.{table}') AND c.name = '{column}'
            """);

        return rows.Single();
    }

    /// <summary>The non-key indexes of <paramref name="tables"/> as index name to <c>[UNIQUE ](keys)[ WHERE filter]</c>.</summary>
    private static async Task<Dictionary<string, string>> IndexesAsync(AuthDbContext context, IReadOnlyList<string> tables)
    {
        var tableList = string.Join(", ", tables.Select(table => $"'{table}'"));
        var rows = await StringsAsync(
            context,
            $"""
            SELECT i.name, CASE WHEN i.is_unique = 1 THEN 'UNIQUE ' ELSE '' END, i.filter_definition, c.name, CAST(ic.is_descending_key AS int)
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE OBJECT_SCHEMA_NAME(i.object_id) = 'auth' AND OBJECT_NAME(i.object_id) IN ({tableList})
              AND i.is_primary_key = 0 AND i.type > 0 AND ic.is_included_column = 0
            ORDER BY i.name, ic.key_ordinal
            """,
            columns: 5);

        var definitions = new Dictionary<string, string>();
        foreach (var group in rows.GroupBy(row => row[0]))
        {
            var keys = group.Select(row => row[3] + (row[4] == "1" ? " DESC" : string.Empty));
            var first = group.First();
            definitions[group.Key] = $"{first[1]}({string.Join(", ", keys)})" + (first[2].Length > 0 ? $" WHERE {first[2]}" : string.Empty);
        }

        return definitions;
    }

    private static async Task<List<string>> StringsAsync(AuthDbContext context, string sql)
        => (await StringsAsync(context, sql, columns: 1)).Select(row => row[0]).ToList();

    private static async Task<List<string[]>> StringsAsync(AuthDbContext context, string sql, int columns)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(Ct);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var result = new List<string[]>();
        while (await reader.ReadAsync(Ct))
        {
            result.Add([.. Enumerable.Range(0, columns).Select(index => reader.IsDBNull(index) ? string.Empty : Convert.ToString(reader.GetValue(index)) ?? string.Empty)]);
        }

        return result;
    }

    private UserMfaMethod ConfirmedMethod(User user, out long step)
    {
        var method = UserMfaMethod.StartTotp(user.Id, "Phone", "protected-secret", Now);
        step = Totp.TimeStepAt(Now);
        method.Confirm(step, Now).IsSuccess.ShouldBeTrue();
        return method;
    }

    /// <summary>The user's TOTP method, tracked by a scope of its own (a request that loaded it and has not saved yet).</summary>
    private async Task<(IServiceProvider Scope, UserMfaMethod Method)> LoadTotpAsync(User user)
    {
        var scope = NewScope();
        var method = (await scope.GetRequiredService<IMfaMethodRepository>().GetTotpAsync(user.Id, Ct)).ShouldNotBeNull();
        return (scope, method);
    }

    private MfaChallenge NewChallenge(User user, byte[] tokenHash)
        => MfaChallenge.Issue(user.Id, tokenHash, user.SecurityStamp, "Device", ChallengeLifetime, Now);

    private User NewUser(string email) => User.Register(email, "Test user", "en", "hash-1", Now).Value;

    private IServiceProvider NewScope()
    {
        var scope = Factory.Services.CreateAsyncScope();
        _scopes.Add(scope);
        return scope.ServiceProvider;
    }

    private async Task SaveAsync(params object[] entities)
    {
        var context = NewScope().GetRequiredService<AuthDbContext>();
        context.AddRange(entities);
        await context.SaveChangesAsync(Ct);
    }
}
