using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Sessions.Events;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

public sealed class AuthPersistenceTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const int SqlUniqueIndexViolation = 2601;

    private static readonly TimeSpan SlidingLifetime = TimeSpan.FromDays(14);
    private static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(90);

    private readonly List<AsyncServiceScope> _scopes = [];

    private DateTimeOffset Now => Factory.Time.GetUtcNow();

    [Fact]
    public async Task Registered_user_round_trips_with_history_and_role()
    {
        var editors = Role.Create("Editors", "Edits content", Now).Value;
        var viewers = Role.Create("Viewers", "Reads content", Now).Value;
        var user = NewUser("Alice@Example.com", "hash-1");
        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        user.ChangePassword("hash-2", historyCount: 5, Now);
        user.AssignRole(editors.Id, assignedBy: null, Now);
        user.AssignRole(viewers.Id, assignedBy: null, Now);
        await SaveAsync(editors, viewers, user);

        var saved = await NewScope().GetRequiredService<IUserRepository>().GetByIdAsync(user.Id, Ct);

        saved.ShouldNotBeNull();
        saved.Email.ShouldBe("Alice@Example.com");
        saved.NormalizedEmail.ShouldBe("ALICE@EXAMPLE.COM");
        saved.PasswordHash.ShouldBe("hash-2");
        saved.SecurityStamp.ShouldBe(user.SecurityStamp);
        saved.DisplayName.ShouldBe("Test user");
        saved.Locale.ShouldBe("en");
        saved.TimeZone.ShouldBe("UTC");
        saved.Status.ShouldBe(UserStatus.Active);
        saved.PasswordChangedAt.ShouldBe(Now);
        saved.RowVersion.ShouldNotBeEmpty();
        saved.PasswordHistory.Select(entry => entry.PasswordHash).ShouldBe(["hash-1", "hash-2"]);
        saved.Roles.Select(role => role.RoleId).ShouldBe([editors.Id, viewers.Id], ignoreOrder: true);
        var byEmail = await NewScope().GetRequiredService<IUserRepository>().GetByNormalizedEmailAsync("ALICE@EXAMPLE.COM", Ct);
        byEmail.ShouldNotBeNull().Id.ShouldBe(user.Id);

        // The backing collections are tracked: trimming the history and removing a role delete those rows.
        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        var scope = NewScope();
        var loaded = (await scope.GetRequiredService<IUserRepository>().GetByIdAsync(user.Id, Ct)).ShouldNotBeNull();
        loaded.ChangePassword("hash-3", historyCount: 2, Now);
        loaded.RemoveRole(editors.Id);
        await scope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);

        var changed = (await NewScope().GetRequiredService<IUserRepository>().GetByIdAsync(user.Id, Ct)).ShouldNotBeNull();
        changed.PasswordHistory.Select(entry => entry.PasswordHash).ShouldBe(["hash-2", "hash-3"]);
        changed.Roles.ShouldHaveSingleItem().RoleId.ShouldBe(viewers.Id);
        var roleUsers = await NewScope().GetRequiredService<IRoleRepository>().GetUserIdsInRoleAsync(viewers.Id, Ct);
        roleUsers.ShouldBe([user.Id]);
    }

    [Fact]
    public async Task Session_with_tokens_round_trips()
    {
        var user = NewUser("dave@example.com");
        var (session, first) = UserSession.Start(
            user.Id, "pwd", "Firefox on Windows", "Mozilla/5.0", "203.0.113.7", user.SecurityStamp, TokenHash(1), SlidingLifetime, AbsoluteLifetime, Now);
        await SaveAsync(user, session);

        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        var rotateScope = NewScope();
        var loaded = await rotateScope.GetRequiredService<ISessionRepository>().GetByRefreshTokenHashAsync(TokenHash(1), Ct);
        var second = loaded.ShouldNotBeNull().Rotate(TokenHash(1), TokenHash(2), user.SecurityStamp, SlidingLifetime, Now).Value;
        await rotateScope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);

        var saved = (await NewScope().GetRequiredService<ISessionRepository>().GetByIdAsync(session.Id, Ct)).ShouldNotBeNull();
        saved.UserId.ShouldBe(user.Id);
        saved.AuthMethods.ShouldBe("pwd");
        saved.DeviceName.ShouldBe("Firefox on Windows");
        saved.UserAgent.ShouldBe("Mozilla/5.0");
        saved.IpAddress.ShouldBe("203.0.113.7");
        saved.SecurityStamp.ShouldBe(user.SecurityStamp);
        saved.CreatedAt.ShouldBe(session.CreatedAt);
        saved.LastSeenAt.ShouldBe(Now);
        saved.ExpiresAt.ShouldBe(session.ExpiresAt);
        saved.RevokedAt.ShouldBeNull();
        saved.RefreshTokens.Count.ShouldBe(2);
        var used = saved.RefreshTokens.Single(token => token.Id == first.Id);
        used.TokenHash.ShouldBe(TokenHash(1));
        used.UsedAt.ShouldBe(Now);
        used.ReplacedByTokenId.ShouldBe(second.Id);
        saved.RefreshTokens.Single(token => token.Id == second.Id).ExpiresAt.ShouldBe(Now + SlidingLifetime);

        // Looked up by any token of the chain, the session comes back with the whole chain (Ruling R7).
        var byOldToken = await NewScope().GetRequiredService<ISessionRepository>().GetByRefreshTokenHashAsync(TokenHash(1), Ct);
        byOldToken.ShouldNotBeNull().RefreshTokens.Count.ShouldBe(2);
        (await NewScope().GetRequiredService<ISessionRepository>().GetByRefreshTokenHashAsync(TokenHash(99), Ct)).ShouldBeNull();
        var active = await NewScope().GetRequiredService<ISessionRepository>().GetActiveByUserAsync(user.Id, Now, Ct);
        active.ShouldHaveSingleItem().RefreshTokens.Count.ShouldBe(2);

        // Revoking the loaded aggregate revokes every token of the chain.
        var revokeScope = NewScope();
        var toRevoke = (await revokeScope.GetRequiredService<ISessionRepository>().GetByIdAsync(session.Id, Ct)).ShouldNotBeNull();
        toRevoke.Revoke(SessionRevokedReason.Logout, Now);
        await revokeScope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);

        var revoked = (await NewScope().GetRequiredService<ISessionRepository>().GetByIdAsync(session.Id, Ct)).ShouldNotBeNull();
        revoked.RevokedReason.ShouldBe(SessionRevokedReason.Logout);
        revoked.RefreshTokens.ShouldAllBe(token => token.RevokedAt == Now);
        (await NewScope().GetRequiredService<ISessionRepository>().GetActiveByUserAsync(user.Id, Now, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Over_long_client_values_are_saved_cut_to_their_columns()
    {
        var user = NewUser("ivan@example.com");
        var (session, _) = UserSession.Start(
            user.Id, "pwd", "Device", new string('u', 600), new string('i', 100), user.SecurityStamp, TokenHash(1), SlidingLifetime, AbsoluteLifetime, Now);
        await SaveAsync(user, session);

        var saved = (await NewScope().GetRequiredService<ISessionRepository>().GetByIdAsync(session.Id, Ct)).ShouldNotBeNull();

        // nvarchar(512) and nvarchar(45): a longer value would fail the save instead.
        saved.UserAgent.ShouldBe(new string('u', 512));
        saved.IpAddress.ShouldBe(new string('i', 45));
    }

    [Fact]
    public async Task Duplicate_normalized_email_violates_the_unique_index()
    {
        await SaveAsync(NewUser("bob@example.com"));

        var exception = await Should.ThrowAsync<DbUpdateException>(() => SaveAsync(NewUser(" BOB@Example.com ")));

        exception.InnerException.ShouldBeOfType<SqlException>().Number.ShouldBe(SqlUniqueIndexViolation);
    }

    [Fact]
    public async Task Soft_deleted_user_frees_the_email_and_is_hidden_from_queries()
    {
        var original = NewUser("carol@example.com");
        await SaveAsync(original);

        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        var deleteScope = NewScope();
        var loaded = (await deleteScope.GetRequiredService<IUserRepository>().GetByIdAsync(original.Id, Ct)).ShouldNotBeNull();
        deleteScope.GetRequiredService<AuthDbContext>().Remove(loaded);
        await deleteScope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);

        var users = NewScope().GetRequiredService<IUserRepository>();
        (await users.GetByIdAsync(original.Id, Ct)).ShouldBeNull();
        (await users.GetByNormalizedEmailAsync("CAROL@EXAMPLE.COM", Ct)).ShouldBeNull();

        var replacement = NewUser("carol@example.com");
        await SaveAsync(replacement);

        var found = await NewScope().GetRequiredService<IUserRepository>().GetByNormalizedEmailAsync("CAROL@EXAMPLE.COM", Ct);
        found.ShouldNotBeNull().Id.ShouldBe(replacement.Id);

        // The deleted row and its history stay: a soft delete is an update of the user only.
        var context = NewScope().GetRequiredService<AuthDbContext>();
        var deleted = await context.Set<User>().IgnoreQueryFilters().SingleAsync(user => user.Id == original.Id, Ct);
        deleted.IsDeleted.ShouldBeTrue();
        deleted.DeletedAt.ShouldBe(Now.UtcDateTime);
        (await context.Set<PasswordHistoryEntry>().CountAsync(entry => entry.UserId == original.Id, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task DateTime_values_round_trip_as_utc()
    {
        // A value with a non-UTC offset is stored as its UTC instant in datetime2(3) and read back with offset zero.
        var malaysia = TimeSpan.FromHours(8);
        Factory.Time.Advance(TimeSpan.FromMilliseconds(123));
        var localNow = Now.ToOffset(malaysia);
        var user = NewUser("erin@example.com");
        user.RecordFailedSignIn(localNow, maxFailedAttempts: 1, lockoutDuration: TimeSpan.FromMinutes(15));
        var (session, token) = UserSession.Start(
            user.Id, "pwd", "Device", null, null, user.SecurityStamp, TokenHash(1), SlidingLifetime, AbsoluteLifetime, localNow);
        var entry = AuthAuditLog.Create(AuthAuditEvents.LoginFailed, succeeded: false, localNow, user.Id);

        var scope = NewScope();
        scope.GetRequiredService<IUserRepository>().Add(user);
        scope.GetRequiredService<ISessionRepository>().Add(session);
        scope.GetRequiredService<IAuthAuditWriter>().Record(entry);
        await scope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);

        var context = NewScope().GetRequiredService<AuthDbContext>();
        var savedUser = await context.Set<User>().SingleAsync(saved => saved.Id == user.Id, Ct);
        var savedToken = await context.Set<RefreshToken>().SingleAsync(saved => saved.Id == token.Id, Ct);
        var savedEntry = await context.Set<AuthAuditLog>().SingleAsync(saved => saved.Id == entry.Id, Ct);

        savedUser.LockoutEnd.ShouldBe(Now + TimeSpan.FromMinutes(15));
        savedUser.LockoutEnd!.Value.Offset.ShouldBe(TimeSpan.Zero);
        savedUser.CreatedAt.ShouldBe(Now.UtcDateTime);
        savedUser.CreatedAt.Kind.ShouldBe(DateTimeKind.Utc);
        savedToken.ExpiresAt.ShouldBe(Now + SlidingLifetime);
        savedToken.ExpiresAt.Offset.ShouldBe(TimeSpan.Zero);
        savedEntry.OccurredAt.ShouldBe(Now);
        savedEntry.OccurredAt.Offset.ShouldBe(TimeSpan.Zero);
        savedEntry.OccurredAt.UtcDateTime.Kind.ShouldBe(DateTimeKind.Utc);

        var columnTypes = await context.Database.SqlQuery<string>(
            $"""
            SELECT CONCAT(TABLE_NAME, '.', COLUMN_NAME, ' ', DATA_TYPE, '(', DATETIME_PRECISION, ')') AS [Value]
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'auth'
              AND COLUMN_NAME IN ('LockoutEnd', 'ExpiresAt', 'OccurredAt', 'CreatedAt')
              AND TABLE_NAME IN ('Users', 'RefreshTokens', 'AuthAuditLogs')
            """).ToListAsync(Ct);
        columnTypes.ShouldBe(
            [
                "AuthAuditLogs.OccurredAt datetime2(3)",
                "RefreshTokens.CreatedAt datetime2(3)",
                "RefreshTokens.ExpiresAt datetime2(3)",
                "Users.CreatedAt datetime2(3)",
                "Users.LockoutEnd datetime2(3)",
            ],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Saving_a_new_user_writes_one_outbox_message_in_the_same_save()
    {
        var user = NewUser("frank@example.com");

        var scope = NewScope();
        scope.GetRequiredService<IUserRepository>().Add(user);
        await scope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);

        var context = NewScope().GetRequiredService<AuthDbContext>();
        (await context.Set<User>().CountAsync(Ct)).ShouldBe(1);
        var message = await context.Set<OutboxMessage>().SingleAsync(Ct);
        message.Type.ShouldBe(typeof(UserRegisteredDomainEvent).FullName);
        message.Content.ShouldContain(user.Id.ToString());
        message.ProcessedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Try_claim_refresh_token_succeeds_for_exactly_one_of_two_concurrent_callers()
    {
        var user = NewUser("grace@example.com");
        var (session, first) = UserSession.Start(
            user.Id, "pwd", "Device", null, null, user.SecurityStamp, TokenHash(1), SlidingLifetime, AbsoluteLifetime, Now);
        await SaveAsync(user, session);
        Factory.Time.Advance(TimeSpan.FromSeconds(5));

        var claims = await Task.WhenAll(
            NewScope().GetRequiredService<ISessionRepository>().TryClaimRefreshTokenAsync(first.Id, Now, Ct),
            NewScope().GetRequiredService<ISessionRepository>().TryClaimRefreshTokenAsync(first.Id, Now, Ct));

        claims.Count(claimed => claimed).ShouldBe(1);
        (await NewScope().GetRequiredService<ISessionRepository>().TryClaimRefreshTokenAsync(first.Id, Now, Ct)).ShouldBeFalse();
        (await NewScope().GetRequiredService<ISessionRepository>().TryClaimRefreshTokenAsync(Guid.NewGuid(), Now, Ct)).ShouldBeFalse();
        var stored = await NewScope().GetRequiredService<AuthDbContext>().Set<RefreshToken>().SingleAsync(token => token.Id == first.Id, Ct);
        stored.UsedAt.ShouldBe(Now);
    }

    [Fact]
    public async Task Try_claim_refresh_token_leaves_the_loaded_session_untouched()
    {
        var user = NewUser("heidi@example.com");
        var (session, first) = UserSession.Start(
            user.Id, "pwd", "Device", null, null, user.SecurityStamp, TokenHash(1), SlidingLifetime, AbsoluteLifetime, Now);
        await SaveAsync(user, session);
        Factory.Time.Advance(TimeSpan.FromSeconds(5));

        // The refresh handler order (Ruling R7): load the session, claim in SQL, then rotate the instance it already holds.
        var scope = NewScope();
        var sessions = scope.GetRequiredService<ISessionRepository>();
        var loaded = (await sessions.GetByRefreshTokenHashAsync(TokenHash(1), Ct)).ShouldNotBeNull();
        (await sessions.TryClaimRefreshTokenAsync(first.Id, Now, Ct)).ShouldBeTrue();

        loaded.RefreshTokens.Single().UsedAt.ShouldBeNull();
        loaded.Rotate(TokenHash(1), TokenHash(2), user.SecurityStamp, SlidingLifetime, Now).IsSuccess.ShouldBeTrue();
        await scope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);

        var saved = (await NewScope().GetRequiredService<ISessionRepository>().GetByIdAsync(session.Id, Ct)).ShouldNotBeNull();
        saved.RefreshTokens.Count.ShouldBe(2);
        saved.RevokedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Winning_rotation_and_losing_reuse_revocation_both_persist_in_either_save_order(bool winnerSavesFirst)
    {
        var user = NewUser("ivan@example.com");
        var (session, first) = UserSession.Start(
            user.Id, "pwd", "Device", null, null, user.SecurityStamp, TokenHash(1), SlidingLifetime, AbsoluteLifetime, Now);
        await SaveAsync(user, session);
        Factory.Time.Advance(TimeSpan.FromSeconds(5));

        // Two refreshes with the same token, interleaved as the handler can be: both load, both pass the checks, one claim wins.
        var winnerScope = NewScope();
        var loserScope = NewScope();
        var winner = (await winnerScope.GetRequiredService<ISessionRepository>().GetByRefreshTokenHashAsync(TokenHash(1), Ct)).ShouldNotBeNull();
        var loser = (await loserScope.GetRequiredService<ISessionRepository>().GetByRefreshTokenHashAsync(TokenHash(1), Ct)).ShouldNotBeNull();
        winner.ValidateRefresh(TokenHash(1), user.SecurityStamp, Now).IsSuccess.ShouldBeTrue();
        loser.ValidateRefresh(TokenHash(1), user.SecurityStamp, Now).IsSuccess.ShouldBeTrue();
        (await winnerScope.GetRequiredService<ISessionRepository>().TryClaimRefreshTokenAsync(first.Id, Now, Ct)).ShouldBeTrue();
        (await loserScope.GetRequiredService<ISessionRepository>().TryClaimRefreshTokenAsync(first.Id, Now, Ct)).ShouldBeFalse();
        winner.Rotate(TokenHash(1), TokenHash(2), user.SecurityStamp, SlidingLifetime, Now).IsSuccess.ShouldBeTrue();
        loser.ReportTokenReuse(Now);

        // Neither save may throw (no 500) or undo the other: the session has no concurrency token and each writes its own columns.
        var saves = winnerSavesFirst ? new[] { winnerScope, loserScope } : [loserScope, winnerScope];
        foreach (var scope in saves)
        {
            await scope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }

        var stored = (await NewScope().GetRequiredService<ISessionRepository>().GetByIdAsync(session.Id, Ct)).ShouldNotBeNull();
        stored.RevokedAt.ShouldBe(Now);
        stored.RevokedReason.ShouldBe(SessionRevokedReason.TokenReuse);
        stored.LastSeenAt.ShouldBe(Now);
        stored.RefreshTokens.Count.ShouldBe(2);
        var storedFirst = stored.RefreshTokens.Single(token => token.Id == first.Id);
        storedFirst.UsedAt.ShouldBe(Now);
        storedFirst.RevokedAt.ShouldBe(Now);

        // The winner's new token is dead with its session, whether or not its own row was revoked.
        stored.ValidateRefresh(TokenHash(2), user.SecurityStamp, Now).Error.ShouldBe(SessionErrors.InvalidRefreshToken);
        (await NewScope().GetRequiredService<AuthDbContext>().Set<OutboxMessage>()
            .CountAsync(message => message.Type == typeof(RefreshTokenReuseDetectedDomainEvent).FullName, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Data_protection_key_ring_is_stored_in_the_auth_schema()
    {
        var keyManager = Factory.Services.GetRequiredService<IKeyManager>();

        keyManager.CreateNewKey(Now, Now.AddDays(90));

        var context = NewScope().GetRequiredService<AuthDbContext>();
        var keyCount = await context.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [auth].[DataProtectionKeys]")
            .SingleAsync(Ct);
        keyCount.ShouldBe(1);
        (await context.DataProtectionKeys.SingleAsync(Ct)).Xml.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task TrySaveChanges_answers_false_on_a_concurrency_conflict_and_saves_nothing()
    {
        // Only a successful sign-in still changes the user through the change tracker; two at once conflict on RowVersion.
        var user = NewUser("alice@example.com");
        await SaveAsync(user);
        var firstScope = NewScope();
        var first = (await firstScope.GetRequiredService<IUserRepository>().GetByIdAsync(user.Id, Ct)).ShouldNotBeNull();
        var secondScope = NewScope();
        var second = (await secondScope.GetRequiredService<IUserRepository>().GetByIdAsync(user.Id, Ct)).ShouldNotBeNull();

        first.RecordSuccessfulSignIn(Now);
        (await firstScope.GetRequiredService<IUnitOfWork>().TrySaveChangesAsync(Ct)).ShouldBeTrue();
        second.RecordSuccessfulSignIn(Now.AddSeconds(1));
        secondScope.GetRequiredService<IAuthAuditWriter>().Record(AuthAuditLog.Create(AuthAuditEvents.LoginSucceeded, succeeded: true, Now, user.Id));

        (await secondScope.GetRequiredService<IUnitOfWork>().TrySaveChangesAsync(Ct)).ShouldBeFalse();

        var saved = (await NewScope().GetRequiredService<IUserRepository>().GetByIdAsync(user.Id, Ct)).ShouldNotBeNull();
        saved.LastLoginAt.ShouldBe(Now);
        (await NewScope().GetRequiredService<AuthDbContext>().Set<AuthAuditLog>().CountAsync(Ct)).ShouldBe(0);
    }

    /// <summary>
    /// The atomic SQL count must be exactly <see cref="User.RecordFailedSignIn"/>: each state is built through the domain, then the
    /// next failure is applied once through the domain (in memory) and once through the repository (in the database), and the results
    /// must agree. Threshold 5 and 15 minutes unless the state says otherwise.
    /// </summary>
    [Theory]
    [InlineData("never failed")]
    [InlineData("one failure")]
    [InlineData("one below the threshold")]
    [InlineData("at the threshold without a lockout")]
    [InlineData("currently locked")]
    [InlineData("locked until one tick from now")]
    [InlineData("lock expired exactly now")]
    [InlineData("lock expired long ago")]
    public async Task Atomic_failed_sign_in_count_matches_the_domain_rule(string state)
    {
        var lockoutDuration = TimeSpan.FromMinutes(15);
        var maxFailedAttempts = state == "at the threshold without a lockout" ? 3 : 5;
        var (priorFailures, elapsed) = state switch
        {
            "never failed" => (0, TimeSpan.Zero),
            "one failure" => (1, TimeSpan.FromMinutes(1)),
            "one below the threshold" => (4, TimeSpan.FromMinutes(1)),
            "at the threshold without a lockout" => (4, TimeSpan.FromMinutes(1)),
            "currently locked" => (5, TimeSpan.FromMinutes(5)),
            "locked until one tick from now" => (5, lockoutDuration - TimeSpan.FromTicks(1)),
            "lock expired exactly now" => (5, lockoutDuration),
            "lock expired long ago" => (5, TimeSpan.FromDays(2)),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
        };

        // The prior failures are counted with the default threshold of 5, as they were when they happened.
        var start = Now;
        var user = NewUser("alice@example.com");
        for (var failure = 0; failure < priorFailures; failure++)
        {
            user.RecordFailedSignIn(start, maxFailedAttempts: 5, lockoutDuration);
        }

        await SaveAsync(user);
        var now = start + elapsed;

        var inMemory = (await NewScope().GetRequiredService<IUserRepository>().GetByIdAsync(user.Id, Ct)).ShouldNotBeNull();
        var domainLocked = inMemory.RecordFailedSignIn(now, maxFailedAttempts, lockoutDuration);
        var counted = await NewScope().GetRequiredService<IUserRepository>()
            .RecordFailedSignInAsync(user.Id, now, maxFailedAttempts, lockoutDuration, Ct);
        var stored = (await NewScope().GetRequiredService<IUserRepository>().GetByIdAsync(user.Id, Ct)).ShouldNotBeNull();

        stored.AccessFailedCount.ShouldBe(inMemory.AccessFailedCount, state);
        stored.LockoutEnd.ShouldBe(inMemory.LockoutEnd, state);
        (counted?.LockedOut ?? false).ShouldBe(domainLocked, state);
        if (counted is null)
        {
            // Nothing was counted: only a locked account is left alone.
            inMemory.IsLockedOut(now).ShouldBeTrue(state);
        }
        else
        {
            counted.AccessFailedCount.ShouldBe(stored.AccessFailedCount, state);
            counted.LockoutEnd.ShouldBe(stored.LockoutEnd, state);
        }
    }

    [Fact]
    public async Task Atomic_failed_sign_in_skips_a_soft_deleted_user()
    {
        var user = NewUser("alice@example.com");
        await SaveAsync(user);
        var context = NewScope().GetRequiredService<AuthDbContext>();
        context.Remove(await context.Set<User>().SingleAsync(candidate => candidate.Id == user.Id, Ct));
        await context.SaveChangesAsync(Ct);

        var counted = await NewScope().GetRequiredService<IUserRepository>()
            .RecordFailedSignInAsync(user.Id, Now, maxFailedAttempts: 5, TimeSpan.FromMinutes(15), Ct);

        counted.ShouldBeNull();
        var stored = await NewScope().GetRequiredService<AuthDbContext>().Set<User>().IgnoreQueryFilters()
            .SingleAsync(candidate => candidate.Id == user.Id, Ct);
        stored.AccessFailedCount.ShouldBe(0);
    }

    [Fact]
    public async Task Count_made_in_the_database_is_not_overwritten_by_the_tracked_user()
    {
        // The login failure path: the user is tracked (loaded before the count), the count happens in SQL, the lockout event is raised
        // on the tracked instance and the audit row is added; the save writes the event and the audit row and leaves the count alone.
        var user = NewUser("alice@example.com");
        for (var failure = 0; failure < 4; failure++)
        {
            user.RecordFailedSignIn(Now, maxFailedAttempts: 5, TimeSpan.FromMinutes(15));
        }

        user.ClearDomainEvents();
        await SaveAsync(user);
        var scope = NewScope();
        var tracked = (await scope.GetRequiredService<IUserRepository>().GetByIdAsync(user.Id, Ct)).ShouldNotBeNull();

        var counted = (await scope.GetRequiredService<IUserRepository>()
            .RecordFailedSignInAsync(user.Id, Now, maxFailedAttempts: 5, TimeSpan.FromMinutes(15), Ct)).ShouldNotBeNull();
        counted.LockedOut.ShouldBeTrue();
        tracked.NoteLockedOut(counted.LockoutEnd!.Value);
        scope.GetRequiredService<IAuthAuditWriter>().Record(AuthAuditLog.Create(AuthAuditEvents.LockedOut, succeeded: false, Now, user.Id));
        await scope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);

        var stored = (await NewScope().GetRequiredService<IUserRepository>().GetByIdAsync(user.Id, Ct)).ShouldNotBeNull();
        stored.AccessFailedCount.ShouldBe(5);
        stored.LockoutEnd.ShouldBe(Now + TimeSpan.FromMinutes(15));
        var outbox = NewScope().GetRequiredService<AuthDbContext>().Set<OutboxMessage>();
        (await outbox.CountAsync(message => message.Type.Contains(nameof(UserLockedOutDomainEvent)), Ct)).ShouldBe(1);
        (await NewScope().GetRequiredService<AuthDbContext>().Set<AuthAuditLog>().CountAsync(Ct)).ShouldBe(1);
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes)
        {
            await scope.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    private static byte[] TokenHash(int seed) => SHA256.HashData(BitConverter.GetBytes(seed));

    private User NewUser(string email, string? passwordHash = "hash-1")
        => User.Register(email, "Test user", "en", passwordHash, Now).Value;

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
