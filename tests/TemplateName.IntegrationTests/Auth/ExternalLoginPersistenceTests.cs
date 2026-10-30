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
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

public sealed class ExternalLoginPersistenceTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const int SqlUniqueIndexViolation = 2601;
    private const int SqlStringTruncated = 2628;
    private const int SqlStringTruncatedLegacy = 8152;
    private const string PreviousMigration = "AddCleanupIndexes";

    private readonly List<AsyncServiceScope> _scopes = [];

    private DateTimeOffset Now => Factory.Time.GetUtcNow();

    [Fact]
    public async Task Duplicate_provider_and_subject_is_refused_by_the_index()
    {
        var first = NewUser("alice@example.com");
        first.LinkExternalLogin("google", "subject-1", "alice@example.com", Now).IsSuccess.ShouldBeTrue();
        await SaveAsync(first);

        // Two users linking the same identity at once both passed the lookup; the unique index lets one insert win.
        var second = NewUser("bob@example.com");
        second.LinkExternalLogin("google", "subject-1", "bob@example.com", Now).IsSuccess.ShouldBeTrue();
        var exception = await Should.ThrowAsync<DbUpdateException>(() => SaveAsync(second));

        exception.InnerException.ShouldBeOfType<SqlException>().Number.ShouldBe(SqlUniqueIndexViolation);
        exception.InnerException.Message.ShouldContain($"'{UniqueIndexNames.ExternalLoginIdentity}'");
        (await NewScope().GetRequiredService<AuthDbContext>().Set<User>().CountAsync(user => user.Id == second.Id, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_user_cannot_hold_two_logins_for_one_provider()
    {
        var user = NewUser("alice@example.com");
        user.LinkExternalLogin("google", "subject-1", null, Now).IsSuccess.ShouldBeTrue();
        await SaveAsync(user);

        // The domain refuses a second subject for a provider; a stale aggregate that did not see the first login is stopped by the index.
        var stale = NewScope().GetRequiredService<AuthDbContext>();
        stale.Add(UserExternalLoginRow(user.Id, "google", "subject-2"));
        var exception = await Should.ThrowAsync<DbUpdateException>(() => stale.SaveChangesAsync(Ct));

        exception.InnerException.ShouldBeOfType<SqlException>().Number.ShouldBe(SqlUniqueIndexViolation);
        exception.InnerException.Message.ShouldContain($"'{UniqueIndexNames.ExternalLoginProviderPerUser}'");

        // Another provider for the same user is fine.
        var scope = NewScope();
        var loaded = (await scope.GetRequiredService<IUserRepository>().GetByIdAsync(user.Id, Ct)).ShouldNotBeNull();
        loaded.LinkExternalLogin("microsoft", "subject-3", null, Now).IsSuccess.ShouldBeTrue();
        await scope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        (await NewScope().GetRequiredService<IUserRepository>().GetByIdAsync(user.Id, Ct)).ShouldNotBeNull().ExternalLogins.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetByExternalLoginAsync_loads_the_user_with_roles_and_logins()
    {
        var role = Role.Create("Editors", "Edits content", Now).Value;
        var user = NewUser("alice@example.com");
        var other = NewUser("bob@example.com");
        user.AssignRole(role.Id, assignedBy: null, Now);
        user.LinkExternalLogin("google", "subject-1", "alice@gmail.example", Now).IsSuccess.ShouldBeTrue();
        user.LinkExternalLogin("apple", "subject-2", null, Now).IsSuccess.ShouldBeTrue();
        other.LinkExternalLogin("google", "subject-3", null, Now).IsSuccess.ShouldBeTrue();
        await SaveAsync(role, user, other);

        var found = (await NewScope().GetRequiredService<IUserRepository>().GetByExternalLoginAsync("google", "subject-1", Ct)).ShouldNotBeNull();

        found.Id.ShouldBe(user.Id);
        found.Roles.ShouldHaveSingleItem().RoleId.ShouldBe(role.Id);
        found.PasswordHistory.ShouldHaveSingleItem();
        found.ExternalLogins.Select(login => (login.Provider, login.Subject)).ShouldBe(
            [("google", "subject-1"), ("apple", "subject-2")],
            ignoreOrder: true);
        var google = found.ExternalLogins.Single(login => login.Provider == "google");
        google.UserId.ShouldBe(user.Id);
        google.Email.ShouldBe("alice@gmail.example");
        google.LinkedAt.ShouldBe(Now);
        google.LastUsedAt.ShouldBeNull();

        var repository = NewScope().GetRequiredService<IUserRepository>();
        (await repository.GetByExternalLoginAsync("google", "subject-2", Ct)).ShouldBeNull();
        (await repository.GetByExternalLoginAsync("facebook", "subject-1", Ct)).ShouldBeNull();

        // A soft-deleted user is not found, like everywhere else in the repository.
        var context = NewScope().GetRequiredService<AuthDbContext>();
        context.Remove(await context.Set<User>().SingleAsync(candidate => candidate.Id == other.Id, Ct));
        await context.SaveChangesAsync(Ct);
        (await NewScope().GetRequiredService<IUserRepository>().GetByExternalLoginAsync("google", "subject-3", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Logins_round_trip_and_an_unlinked_login_is_deleted()
    {
        var user = NewUser("alice@example.com");
        var google = user.LinkExternalLogin("google", "subject-1", null, Now).Value;
        user.LinkExternalLogin("apple", "subject-2", null, Now).IsSuccess.ShouldBeTrue();
        await SaveAsync(user);

        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        var scope = NewScope();
        var loaded = (await scope.GetRequiredService<IUserRepository>().GetByIdAsync(user.Id, Ct)).ShouldNotBeNull();
        loaded.ExternalLogins.Single(login => login.Id == google.Id).NoteUsed(Now);
        await scope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);

        var changeScope = NewScope();
        var changed = (await changeScope.GetRequiredService<IUserRepository>().GetByIdAsync(user.Id, Ct)).ShouldNotBeNull();
        changed.ExternalLogins.Single(login => login.Id == google.Id).LastUsedAt.ShouldBe(Now);
        changed.UnlinkExternalLogin(google.Id, Now).IsSuccess.ShouldBeTrue();
        await changeScope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);

        var saved = (await NewScope().GetRequiredService<IUserRepository>().GetByIdAsync(user.Id, Ct)).ShouldNotBeNull();
        saved.ExternalLogins.ShouldHaveSingleItem().Provider.ShouldBe("apple");
    }

    [Fact]
    public async Task Deleting_a_user_row_cascades_to_its_logins()
    {
        var user = NewUser("alice@example.com");
        var other = NewUser("bob@example.com");
        user.LinkExternalLogin("google", "subject-1", null, Now).IsSuccess.ShouldBeTrue();
        user.LinkExternalLogin("apple", "subject-2", null, Now).IsSuccess.ShouldBeTrue();
        other.LinkExternalLogin("google", "subject-3", null, Now).IsSuccess.ShouldBeTrue();
        await SaveAsync(user, other);
        var context = NewScope().GetRequiredService<AuthDbContext>();

        // Users are soft-deleted by the application, so this is the database rule for a row that is removed for good.
        await context.Database.ExecuteSqlAsync($"DELETE FROM [auth].[Users] WHERE [Id] = {user.Id}", Ct);

        var logins = await NewScope().GetRequiredService<AuthDbContext>().Set<UserExternalLogin>().AsNoTracking().ToListAsync(Ct);
        logins.ShouldHaveSingleItem().Subject.ShouldBe("subject-3");
    }

    [Fact]
    public async Task A_soft_deleted_user_keeps_its_logins()
    {
        var user = NewUser("alice@example.com");
        user.LinkExternalLogin("google", "subject-1", null, Now).IsSuccess.ShouldBeTrue();
        await SaveAsync(user);

        var context = NewScope().GetRequiredService<AuthDbContext>();
        context.Remove(await context.Set<User>().Include(candidate => candidate.ExternalLogins).SingleAsync(candidate => candidate.Id == user.Id, Ct));
        await context.SaveChangesAsync(Ct);

        var stored = await NewScope().GetRequiredService<AuthDbContext>().Set<User>().IgnoreQueryFilters()
            .Include(candidate => candidate.ExternalLogins)
            .SingleAsync(candidate => candidate.Id == user.Id, Ct);
        stored.IsDeleted.ShouldBeTrue();
        stored.ExternalLogins.ShouldHaveSingleItem().Subject.ShouldBe("subject-1");
    }

    [Fact]
    public async Task Overlong_subject_is_rejected_by_the_column()
    {
        var user = NewUser("alice@example.com");
        await SaveAsync(user);
        var context = NewScope().GetRequiredService<AuthDbContext>();
        var tooLong = new string('s', UserExternalLogin.SubjectMaxLength + 1);
        var exactly = new string('s', UserExternalLogin.SubjectMaxLength);

        // The domain stops a long subject first, so the column is reached with a statement of its own.
        var exception = await Should.ThrowAsync<SqlException>(() => InsertLoginAsync(context, user.Id, "google", tooLong));

        exception.Number.ShouldBeOneOf(SqlStringTruncated, SqlStringTruncatedLegacy);
        await InsertLoginAsync(context, user.Id, "google", exactly);
        (await NewScope().GetRequiredService<IUserRepository>().GetByExternalLoginAsync("google", exactly, Ct)).ShouldNotBeNull().Id.ShouldBe(user.Id);
    }

    [Fact]
    public async Task SaveChangesUnlessDuplicateAsync_with_several_names_returns_false_for_either_index()
    {
        var holder = NewUser("alice@example.com");
        holder.LinkExternalLogin("google", "subject-1", null, Now).IsSuccess.ShouldBeTrue();
        await SaveAsync(holder);
        string[] both = [UniqueIndexNames.ExternalLoginIdentity, UniqueIndexNames.ExternalLoginProviderPerUser];

        // Refused by (Provider, Subject): another user with the same identity.
        var sameIdentity = NewScope();
        var newcomer = NewUser("bob@example.com");
        newcomer.LinkExternalLogin("google", "subject-1", null, Now).IsSuccess.ShouldBeTrue();
        sameIdentity.GetRequiredService<IUserRepository>().Add(newcomer);
        (await sameIdentity.GetRequiredService<IUnitOfWork>().SaveChangesUnlessDuplicateAsync(both, Ct)).ShouldBeFalse();
        sameIdentity.GetRequiredService<AuthDbContext>().ChangeTracker.Entries().ShouldBeEmpty();

        // Refused by (UserId, Provider): a second subject for a provider the user already has.
        var sameProvider = NewScope();
        sameProvider.GetRequiredService<AuthDbContext>().Add(UserExternalLoginRow(holder.Id, "google", "subject-2"));
        (await sameProvider.GetRequiredService<IUnitOfWork>().SaveChangesUnlessDuplicateAsync(both, Ct)).ShouldBeFalse();

        // A refusal by an index that is not in the list is thrown, like the single-name overload does.
        var emailTaken = NewScope();
        emailTaken.GetRequiredService<IUserRepository>().Add(NewUser("ALICE@example.com"));
        var failure = await Should.ThrowAsync<DbUpdateException>(
            () => emailTaken.GetRequiredService<IUnitOfWork>().SaveChangesUnlessDuplicateAsync(both, Ct));
        failure.InnerException.ShouldBeOfType<SqlException>().Number.ShouldBe(SqlUniqueIndexViolation);

        // Naming the index among others is enough, and a save that conflicts with nothing saves.
        var emailListed = NewScope();
        emailListed.GetRequiredService<IUserRepository>().Add(NewUser("ALICE@example.com"));
        (await emailListed.GetRequiredService<IUnitOfWork>().SaveChangesUnlessDuplicateAsync([UniqueIndexNames.UserEmail, .. both], Ct)).ShouldBeFalse();
        var fresh = NewScope();
        fresh.GetRequiredService<IUserRepository>().Add(NewUser("carol@example.com"));
        (await fresh.GetRequiredService<IUnitOfWork>().SaveChangesUnlessDuplicateAsync(both, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task SaveChangesUnlessDuplicateAsync_refuses_a_blank_name_and_an_empty_list()
    {
        var unitOfWork = NewScope().GetRequiredService<IUnitOfWork>();

        await Should.ThrowAsync<ArgumentException>(() => unitOfWork.SaveChangesUnlessDuplicateAsync(Array.Empty<string>(), Ct));
        await Should.ThrowAsync<ArgumentException>(() => unitOfWork.SaveChangesUnlessDuplicateAsync([" "], Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => unitOfWork.SaveChangesUnlessDuplicateAsync((IReadOnlyCollection<string>)null!, Ct));
    }

    [Fact]
    public async Task Existing_challenges_read_as_pwd()
    {
        // A database of its own on the same server, migrated to the schema before external logins and holding a challenge, then upgraded.
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
            var challengeId = Guid.NewGuid();
            var createdAt = new DateTime(2026, 10, 20, 8, 30, 15, 123, DateTimeKind.Utc);
            await context.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO [auth].[MfaChallenges] ([Id], [UserId], [TokenHash], [SecurityStamp], [DeviceName], [CreatedAt], [ExpiresAt], [FailedAttempts])
                VALUES ({challengeId}, {user.Id}, {SHA256.HashData(challengeId.ToByteArray())}, {user.SecurityStamp}, 'Old device', {createdAt}, {createdAt.AddMinutes(5)}, 0)
                """,
                Ct);

            await migrator.MigrateAsync(cancellationToken: Ct);

            var challenge = await context.Set<MfaChallenge>().AsNoTracking().SingleAsync(candidate => candidate.Id == challengeId, Ct);
            challenge.FirstFactor.ShouldBe("pwd");
            (await ColumnAsync(context, "MfaChallenges", "FirstFactor")).ShouldBe("nvarchar(16) NOT NULL");
        }
        finally
        {
            await context.Database.EnsureDeletedAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Schema_matches_the_design()
    {
        var context = NewScope().GetRequiredService<AuthDbContext>();

        (await ColumnAsync(context, "UserExternalLogins", "Id")).ShouldBe("uniqueidentifier NOT NULL");
        (await ColumnAsync(context, "UserExternalLogins", "UserId")).ShouldBe("uniqueidentifier NOT NULL");
        (await ColumnAsync(context, "UserExternalLogins", "Provider")).ShouldBe("nvarchar(64) NOT NULL");
        (await ColumnAsync(context, "UserExternalLogins", "Subject")).ShouldBe("nvarchar(256) NOT NULL");
        (await ColumnAsync(context, "UserExternalLogins", "Email")).ShouldBe("nvarchar(256) NULL");
        (await ColumnAsync(context, "UserExternalLogins", "LinkedAt")).ShouldBe("datetime2(3) NOT NULL");
        (await ColumnAsync(context, "UserExternalLogins", "LastUsedAt")).ShouldBe("datetime2(3) NULL");
        (await ColumnAsync(context, "MfaChallenges", "FirstFactor")).ShouldBe("nvarchar(16) NOT NULL");
        (await StringsAsync(
            context,
            """
            SELECT dc.definition
            FROM sys.default_constraints dc
            JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
            WHERE dc.parent_object_id = OBJECT_ID('auth.MfaChallenges') AND c.name = 'FirstFactor'
            """)).ShouldBe(["(N'pwd')"]);

        var indexes = await StringsAsync(
            context,
            """
            SELECT i.name + ' ' + CASE WHEN i.is_unique = 1 THEN 'UNIQUE' ELSE 'PLAIN' END + ' ' + STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('auth.UserExternalLogins') AND i.is_primary_key = 0 AND i.type > 0
            GROUP BY i.name, i.is_unique
            """);
        indexes.ShouldBe(
            [
                $"{UniqueIndexNames.ExternalLoginIdentity} UNIQUE Provider,Subject",
                $"{UniqueIndexNames.ExternalLoginProviderPerUser} UNIQUE UserId,Provider",
            ],
            ignoreOrder: true);

        var foreignKeys = await StringsAsync(
            context,
            """
            SELECT OBJECT_NAME(fk.parent_object_id) + ' -> ' + OBJECT_NAME(fk.referenced_object_id) + ' ' + fk.delete_referential_action_desc
            FROM sys.foreign_keys fk
            WHERE OBJECT_NAME(fk.parent_object_id) = 'UserExternalLogins' AND OBJECT_SCHEMA_NAME(fk.parent_object_id) = 'auth'
            """);
        foreignKeys.ShouldBe(["UserExternalLogins -> Users CASCADE"]);
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes)
        {
            await scope.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    private static async Task InsertLoginAsync(AuthDbContext context, Guid userId, string provider, string subject)
        => await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO [auth].[UserExternalLogins] ([Id], [UserId], [Provider], [Subject], [LinkedAt])
            VALUES ({Guid.NewGuid()}, {userId}, {provider}, {subject}, {new DateTime(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc)})
            """,
            Ct);

    private static async Task<string> ColumnAsync(AuthDbContext context, string table, string column)
    {
        var rows = await StringsAsync(
            context,
            $"""
            SELECT CASE
                WHEN t.name = 'nvarchar' THEN 'nvarchar(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
                WHEN t.name = 'datetime2' THEN 'datetime2(' + CAST(c.scale AS varchar(10)) + ')'
                ELSE t.name END + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END
            FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID('auth.{table}') AND c.name = '{column}'
            """);

        return rows.Single();
    }

    private static async Task<List<string>> StringsAsync(AuthDbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(Ct);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var result = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            result.Add(reader.IsDBNull(0) ? string.Empty : Convert.ToString(reader.GetValue(0)) ?? string.Empty);
        }

        return result;
    }

    // A login saved on its own, as a second request would that did not load the user's other logins.
    private UserExternalLogin UserExternalLoginRow(Guid userId, string provider, string subject)
        => UserExternalLogin.Create(userId, provider, subject, email: null, Now);

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
