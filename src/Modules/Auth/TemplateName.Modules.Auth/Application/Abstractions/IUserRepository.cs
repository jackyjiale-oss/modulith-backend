using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>Loads and adds <see cref="User"/> aggregates, each with its password history and role assignments. Soft-deleted users are not found.</summary>
internal interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Finds a user by <see cref="User.NormalizedEmail"/> (see <see cref="User.NormalizeEmail"/>).</summary>
    Task<User?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken);

    /// <summary>
    /// Whether any user, soft-deleted ones included, has <paramref name="normalizedEmail"/>. For the seeder, which must never re-create
    /// an administrator account that someone deliberately deleted.
    /// </summary>
    Task<bool> ExistsByNormalizedEmailIncludingDeletedAsync(string normalizedEmail, CancellationToken cancellationToken);

    /// <summary>
    /// Counts one failed sign-in of the user in a single atomic statement, by the rules of <see cref="User.RecordFailedSignIn"/>, so
    /// simultaneous failures are each counted once and never lost to a concurrency conflict. Returns the new state, or null when nothing
    /// was counted (the account is locked at <paramref name="now"/>, or the user does not exist or is soft-deleted). It bypasses the
    /// change tracker: a tracked instance of the user keeps its old values and must not be changed and saved for this failure; raise the
    /// lockout event on it with <see cref="User.NoteLockedOut"/>.
    /// </summary>
    Task<FailedSignIn?> RecordFailedSignInAsync(
        Guid userId,
        DateTimeOffset now,
        int maxFailedAttempts,
        TimeSpan lockoutDuration,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether the user is active, not soft-deleted and holds the role. For the SuperAdmin rules of the user administration: an account
    /// that cannot sign in any more does not count, even while its cached permissions still let one request through.
    /// </summary>
    Task<bool> IsActiveInRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken);

    /// <summary>Whether a user other than <paramref name="exceptUserId"/> that is active and not soft-deleted holds the role.</summary>
    Task<bool> AnyOtherActiveInRoleAsync(Guid roleId, Guid exceptUserId, CancellationToken cancellationToken);

    void Add(User user);
}
