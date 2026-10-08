using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Passwords;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.Modules.Auth.Infrastructure.Authorization;

/// <summary>
/// Seeds the Auth module (<c>SeedAuthModuleAsync</c>): the system roles, the declared permissions, the grants of the system roles and,
/// when configured, the first administrator. Idempotent. One run is one transaction under an exclusive application lock, so instances
/// that start together seed one after the other and a failed run changes nothing.
/// </summary>
internal sealed partial class AuthSeeder(
    AuthDbContext context,
    IRoleRepository roles,
    IUserRepository users,
    PermissionSynchronizer synchronizer,
    IPasswordHasher passwordHasher,
    IOptions<SeedOptions> seedOptions,
    IOptions<PasswordOptions> passwordOptions,
    TimeProvider timeProvider,
    ILogger<AuthSeeder> logger)
{
    /// <summary>The permissions <c>Admin</c> gets when the role is created. Later runs leave its set to the administrators.</summary>
    internal static readonly IReadOnlyList<string> AdminDefaultPermissions =
    [
        AuthPermissions.UserView,
        AuthPermissions.UserCreate,
        AuthPermissions.UserLock,
        AuthPermissions.UserResetPassword,
        AuthPermissions.UserRevokeSessions,
        AuthPermissions.RoleView,
        AuthPermissions.PermissionView,
        AuthPermissions.AuditView,
    ];

    private const string AdministratorDisplayName = "Administrator";
    private const string AdministratorLocale = "en";

    // Held until the transaction ends. sp_getapplock answers a negative status when the lock is not granted in time.
    private const string AcquireSeedLockSql = """
        DECLARE @result int;
        EXEC @result = sp_getapplock @Resource = N'auth.seed', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 60000;
        IF @result < 0 THROW 51000, N'The Auth seed lock was not granted within 60 seconds.', 1;
        """;

    private static readonly (string Name, string Description)[] SystemRoleDefinitions =
    [
        (SystemRoles.SuperAdmin, "Holds every permission. Its permissions are maintained by the system."),
        (SystemRoles.Admin, "Administers user accounts and reads roles, permissions and the audit log."),
        (SystemRoles.User, "A signed-in user without administrative permissions."),
    ];

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        // Checked before any work, so a bad setting fails the run without touching the database.
        var administrator = ReadAdministratorSettings();

        // The context retries transient failures, so the whole transaction is the unit that is retried.
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(attemptCancellationToken => SeedOnceAsync(administrator, attemptCancellationToken), cancellationToken);
    }

    private async Task SeedOnceAsync((string Email, string Password)? administrator, CancellationToken cancellationToken)
    {
        context.ChangeTracker.Clear();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync(AcquireSeedLockSql, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var activePermissions = await synchronizer.SyncAsync(now, cancellationToken);

        var superAdmin = await EnsureSystemRoleAsync(SystemRoleDefinitions[0], now, cancellationToken);
        var admin = await EnsureSystemRoleAsync(SystemRoleDefinitions[1], now, cancellationToken);
        _ = await EnsureSystemRoleAsync(SystemRoleDefinitions[2], now, cancellationToken);

        // SyncPermissions, not SetPermissions: the seeder is the one writer of SuperAdmin's set, which SetPermissions refuses.
        superAdmin.Role.SyncPermissions([.. activePermissions.Select(permission => permission.Id)]);
        if (admin.IsCreated)
        {
            admin.Role.SyncPermissions(AdminDefaultPermissionIds(activePermissions));
        }

        var seededAdministratorId = administrator is { } settings
            ? await EnsureAdministratorAsync(settings.Email, settings.Password, superAdmin.Role.Id, now, cancellationToken)
            : null;

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        LogSeeded(logger, activePermissions.Count);
        if (seededAdministratorId is { } userId)
        {
            LogAdministratorSeeded(logger, userId);
        }
    }

    private (string Email, string Password)? ReadAdministratorSettings()
    {
        var options = seedOptions.Value;
        var hasEmail = !string.IsNullOrWhiteSpace(options.AdminEmail);
        var hasPassword = !string.IsNullOrWhiteSpace(options.AdminPassword);
        if (!hasEmail || !hasPassword)
        {
            if (hasEmail != hasPassword)
            {
                LogAdministratorSkipped(logger);
            }

            return null;
        }

        var email = options.AdminEmail!.Trim();
        if (email.Length > UserConfiguration.EmailMaxLength || !MailAddress.TryCreate(email, out var address) || address.Address != email)
        {
            throw new InvalidOperationException($"{SeedOptions.SectionName}:{nameof(SeedOptions.AdminEmail)} is not a valid email address.");
        }

        // The password itself never appears in a message or a log.
        var password = options.AdminPassword!;
        var limits = passwordOptions.Value;
        if (password.Length < limits.MinLength || password.Length > limits.MaxLength)
        {
            throw new InvalidOperationException(
                $"{SeedOptions.SectionName}:{nameof(SeedOptions.AdminPassword)} must be {limits.MinLength} to {limits.MaxLength} characters long.");
        }

        return (email, password);
    }

    private async Task<(Role Role, bool IsCreated)> EnsureSystemRoleAsync(
        (string Name, string Description) definition,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var role = await roles.GetByNormalizedNameAsync(Role.NormalizeName(definition.Name), cancellationToken);
        if (role is not null)
        {
            // A custom role with a system role's name would get SuperAdmin's grants without its protection; refuse to guess.
            return role.IsSystem
                ? (role, false)
                : throw new InvalidOperationException(
                    $"The role '{role.Name}' exists but is not a system role. Rename it so the system role '{definition.Name}' can be seeded.");
        }

        role = Role.CreateSystem(definition.Name, definition.Description, now);
        roles.Add(role);
        return (role, true);
    }

    private async Task<Guid?> EnsureAdministratorAsync(string email, string password, Guid superAdminRoleId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await users.GetByNormalizedEmailAsync(User.NormalizeEmail(email), cancellationToken) is not null)
        {
            return null;
        }

        var user = User.Register(email, AdministratorDisplayName, AdministratorLocale, passwordHasher.Hash(password), now).Value;
        user.ConfirmEmail(now);
        user.AssignRole(superAdminRoleId, assignedBy: null, now);
        users.Add(user);
        return user.Id;
    }

    private static Guid[] AdminDefaultPermissionIds(IReadOnlyList<Permission> activePermissions)
    {
        var idsByCode = activePermissions.ToDictionary(permission => permission.Code, permission => permission.Id, StringComparer.Ordinal);

        return
        [
            .. AdminDefaultPermissions.Select(code => idsByCode.TryGetValue(code, out var id)
                ? id
                : throw new InvalidOperationException($"The Admin default permission '{code}' is not declared by any permission source.")),
        ];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded the Auth module: system roles and {PermissionCount} permissions")]
    private static partial void LogSeeded(ILogger logger, int permissionCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded the administrator account {UserId}")]
    private static partial void LogAdministratorSeeded(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "No administrator account seeded: Auth:Seed:AdminEmail and Auth:Seed:AdminPassword must both be set")]
    private static partial void LogAdministratorSkipped(ILogger logger);
}
