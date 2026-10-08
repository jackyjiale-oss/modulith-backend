using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class UserRepository(AuthDbContext context) : IUserRepository
{
    // User.RecordFailedSignIn in one statement, so simultaneous failures queue on the row lock and each is counted (AuthPersistenceTests
    // runs both over the same states). A locked account is not matched, so a failure while locked changes nothing. Otherwise a past
    // lockout (LockoutEnd set but over) restarts the count at 1 and is cleared; the failure that reaches @MaxFailedAttempts sets the
    // lockout. SET expressions read the values from before the update. Raw SQL skips the save interceptors, so it stamps UpdatedAt
    // itself (an anonymous caller: UpdatedBy null) and RowVersion changes as for any update. Soft-deleted users are never matched.
    private const string RecordFailedSignInSql = """
        UPDATE auth.Users
        SET AccessFailedCount = CASE WHEN LockoutEnd IS NULL THEN AccessFailedCount + 1 ELSE 1 END,
            LockoutEnd = CASE
                WHEN (CASE WHEN LockoutEnd IS NULL THEN AccessFailedCount + 1 ELSE 1 END) >= @MaxFailedAttempts THEN @LockoutEnd
                ELSE NULL
            END,
            UpdatedAt = @Now,
            UpdatedBy = NULL
        OUTPUT inserted.AccessFailedCount, inserted.LockoutEnd
        WHERE Id = @Id
          AND IsDeleted = 0
          AND (LockoutEnd IS NULL OR LockoutEnd <= @Now)
        """;

    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Users().SingleOrDefaultAsync(user => user.Id == id, cancellationToken);

    public Task<User?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
        => Users().SingleOrDefaultAsync(user => user.NormalizedEmail == normalizedEmail, cancellationToken);

    public Task<bool> ExistsByNormalizedEmailIncludingDeletedAsync(string normalizedEmail, CancellationToken cancellationToken)
        => context.Set<User>()
            .IgnoreQueryFilters([ModelConventions.SoftDeleteFilterName])
            .AnyAsync(user => user.NormalizedEmail == normalizedEmail, cancellationToken);

    public async Task<FailedSignIn?> RecordFailedSignInAsync(
        Guid userId,
        DateTimeOffset now,
        int maxFailedAttempts,
        TimeSpan lockoutDuration,
        CancellationToken cancellationToken)
    {
        var rows = await context.Database
            .SqlQueryRaw<FailedSignInRow>(
                RecordFailedSignInSql,
                new SqlParameter("@Id", SqlDbType.UniqueIdentifier) { Value = userId },
                DateTime2("@Now", now.UtcDateTime, scale: 7),
                DateTime2("@LockoutEnd", (now + lockoutDuration).UtcDateTime, scale: 3),
                new SqlParameter("@MaxFailedAttempts", SqlDbType.Int) { Value = maxFailedAttempts })
            .ToListAsync(cancellationToken);

        return rows is [var row]
            ? new FailedSignIn(row.AccessFailedCount, row.LockoutEnd is { } end ? new DateTimeOffset(DateTime.SpecifyKind(end, DateTimeKind.Utc)) : null)
            : null;
    }

    public Task<bool> IsActiveInRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken)
        => context.Set<User>().AnyAsync(
            user => user.Id == userId && user.Status == UserStatus.Active && user.Roles.Any(assignment => assignment.RoleId == roleId),
            cancellationToken);

    public Task<bool> AnyOtherActiveInRoleAsync(Guid roleId, Guid exceptUserId, CancellationToken cancellationToken)
        => context.Set<User>().AnyAsync(
            user => user.Id != exceptUserId && user.Status == UserStatus.Active && user.Roles.Any(assignment => assignment.RoleId == roleId),
            cancellationToken);

    public void Add(User user) => context.Set<User>().Add(user);

    // @Now keeps every tick, so the comparison with LockoutEnd is exact; @LockoutEnd has the column's precision, as EF Core sends it.
    private static SqlParameter DateTime2(string name, DateTime value, byte scale)
        => new(name, SqlDbType.DateTime2) { Value = value, Scale = scale };

    // The whole aggregate. The history is ordered oldest first, because ChangePassword trims it from the front.
    private IQueryable<User> Users() => context.Set<User>()
        .Include(user => user.PasswordHistory.OrderBy(entry => entry.CreatedAt).ThenBy(entry => entry.Id))
        .Include(user => user.Roles)
        .AsSingleQuery();

    /// <summary>The <c>OUTPUT</c> row of <see cref="RecordFailedSignInSql"/>; <c>LockoutEnd</c> is UTC.</summary>
    private sealed class FailedSignInRow
    {
        public int AccessFailedCount { get; init; }

        public DateTime? LockoutEnd { get; init; }
    }
}
