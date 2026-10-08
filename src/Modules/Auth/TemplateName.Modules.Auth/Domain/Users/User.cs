using System.Globalization;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Users;

/// <summary>
/// A person who can sign in. The aggregate owns the sign-in state (confirmation, lockout, suspension), the password history and the
/// role assignments. A user without a password hash (created by an administrator) is valid until a password is set.
/// </summary>
internal sealed class User : AggregateRoot<Guid>, IAuditable, ISoftDeletable
{
    /// <summary>The column limit of <see cref="Email"/> and <see cref="NormalizedEmail"/>; the validators refuse longer addresses.</summary>
    public const int MaxEmailLength = 256;

    /// <summary>The column limit of <see cref="DisplayName"/>.</summary>
    public const int MaxDisplayNameLength = 200;

    /// <summary>The column limit of <see cref="Locale"/>.</summary>
    public const int MaxLocaleLength = 16;

    private const string DefaultTimeZone = "UTC";

    private readonly List<PasswordHistoryEntry> _passwordHistory = [];
    private readonly List<UserRole> _roles = [];

    // EF Core materializes the aggregate through this constructor; callers use Register.
    private User()
    {
    }

    public string Email { get; private set; } = string.Empty;

    public string NormalizedEmail { get; private set; } = string.Empty;

    public bool EmailConfirmed { get; private set; }

    public string? PasswordHash { get; private set; }

    /// <summary>Changes whenever the credentials or the account state change, so tokens issued before the change can be refused.</summary>
    public string SecurityStamp { get; private set; } = string.Empty;

    public UserStatus Status { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public string Locale { get; private set; } = string.Empty;

    public string TimeZone { get; private set; } = DefaultTimeZone;

    public DateTimeOffset? PasswordChangedAt { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    public DateTimeOffset? LockoutEnd { get; private set; }

    public int AccessFailedCount { get; private set; }

    /// <summary>The current and previous password hashes, oldest first, newest last.</summary>
    public IReadOnlyCollection<PasswordHistoryEntry> PasswordHistory => _passwordHistory.AsReadOnly();

    public IReadOnlyCollection<UserRole> Roles => _roles.AsReadOnly();

    public byte[] RowVersion { get; private set; } = [];

    public DateTime CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTime? UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public bool IsDeleted { get; private set; }

    public DateTime? DeletedAt { get; private set; }

    public Guid? DeletedBy { get; private set; }

    /// <summary>The form of an email address used for lookups and the unique index: trimmed and upper-case.</summary>
    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();

    public static Result<User> Register(string email, string displayName, string locale, string? passwordHash, DateTimeOffset now)
    {
        var user = new User
        {
            Id = SequentialGuid.Create(now),
            Email = email.Trim(),
            NormalizedEmail = NormalizeEmail(email),
            PasswordHash = passwordHash,
            SecurityStamp = NewSecurityStamp(),
            Status = UserStatus.Active,
            DisplayName = displayName,
            Locale = locale,
            TimeZone = DefaultTimeZone,
        };

        if (passwordHash is not null)
        {
            user._passwordHistory.Add(PasswordHistoryEntry.Create(user.Id, passwordHash, now));
            user.PasswordChangedAt = now;
        }

        user.Raise(new UserRegisteredDomainEvent(user.Id, user.Email, locale));

        return user;
    }

    public Result ConfirmEmail(DateTimeOffset now)
    {
        if (EmailConfirmed)
        {
            return Result.Success();
        }

        EmailConfirmed = true;
        Raise(new EmailConfirmedDomainEvent(Id, Email));

        return Result.Success();
    }

    /// <summary>True while the account is locked: <paramref name="now"/> is before <see cref="LockoutEnd"/>.</summary>
    public bool IsLockedOut(DateTimeOffset now) => LockoutEnd is { } lockoutEnd && now < lockoutEnd;

    public Result EnsureCanSignIn()
    {
        if (Status == UserStatus.Suspended)
        {
            return Result.Failure(UserErrors.AccountInactive);
        }

        if (!EmailConfirmed)
        {
            return Result.Failure(UserErrors.EmailNotVerified);
        }

        return Result.Success();
    }

    /// <summary>Counts a failed sign-in. Returns true when this failure locked the account.</summary>
    public bool RecordFailedSignIn(DateTimeOffset now, int maxFailedAttempts, TimeSpan lockoutDuration)
    {
        if (IsLockedOut(now))
        {
            return false;
        }

        if (LockoutEnd is not null)
        {
            // The previous lockout has expired: this failure starts a new count.
            LockoutEnd = null;
            AccessFailedCount = 0;
        }

        AccessFailedCount++;
        if (AccessFailedCount < maxFailedAttempts)
        {
            return false;
        }

        LockoutEnd = now + lockoutDuration;
        Raise(new UserLockedOutDomainEvent(Id, LockoutEnd.Value));

        return true;
    }

    /// <summary>
    /// Raises <see cref="UserLockedOutDomainEvent"/> for a failure that was counted outside this instance (the atomic count in the
    /// database, by the rules of <see cref="RecordFailedSignIn"/>) and locked the account. Changes no state, so saving this instance
    /// writes only the event and never an outdated count.
    /// </summary>
    public void NoteLockedOut(DateTimeOffset lockoutEnd) => Raise(new UserLockedOutDomainEvent(Id, lockoutEnd));

    public void RecordSuccessfulSignIn(DateTimeOffset now)
    {
        ResetFailures();
        LastLoginAt = now;
    }

    /// <summary>
    /// Replaces the password. The history keeps at most <paramref name="historyCount"/> hashes including the new one, so the oldest
    /// are dropped; the security stamp changes and any lockout ends.
    /// </summary>
    public Result ChangePassword(string newPasswordHash, int historyCount, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newPasswordHash);

        PasswordHash = newPasswordHash;
        PasswordChangedAt = now;
        SecurityStamp = NewSecurityStamp();
        ResetFailures();

        _passwordHistory.Add(PasswordHistoryEntry.Create(Id, newPasswordHash, now));
        var surplus = _passwordHistory.Count - Math.Max(historyCount, 1);
        if (surplus > 0)
        {
            _passwordHistory.RemoveRange(0, surplus);
        }

        Raise(new PasswordChangedDomainEvent(Id, Email, Locale));

        return Result.Success();
    }

    /// <summary>
    /// Stores a new hash of the <b>same</b> password, made after a successful verification whose hash used weaker parameters (a rehash).
    /// Because the password itself did not change, the security stamp, the history, <see cref="PasswordChangedAt"/> and the sign-in state
    /// stay as they are and no event is raised; <see cref="ChangePassword"/> is for a new password.
    /// </summary>
    /// <exception cref="InvalidOperationException">The user has no password to rehash.</exception>
    public void UpgradePasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        if (PasswordHash is null)
        {
            throw new InvalidOperationException("A user without a password has no hash to upgrade.");
        }

        PasswordHash = passwordHash;
    }

    public Result Suspend(DateTimeOffset now)
    {
        if (Status == UserStatus.Suspended)
        {
            return Result.Success();
        }

        Status = UserStatus.Suspended;
        SecurityStamp = NewSecurityStamp();

        return Result.Success();
    }

    /// <summary>Makes the account active again and ends any lockout, whatever its state before.</summary>
    public Result Reinstate(DateTimeOffset now)
    {
        Status = UserStatus.Active;
        ResetFailures();

        return Result.Success();
    }

    public void UpdateProfile(string displayName, string locale, string timeZone)
    {
        DisplayName = displayName;
        Locale = locale;
        TimeZone = timeZone;
    }

    public Result AssignRole(Guid roleId, Guid? assignedBy, DateTimeOffset now)
    {
        if (_roles.Exists(role => role.RoleId == roleId))
        {
            return Result.Success();
        }

        _roles.Add(UserRole.Create(Id, roleId, assignedBy, now));

        return Result.Success();
    }

    public Result RemoveRole(Guid roleId)
    {
        _roles.RemoveAll(role => role.RoleId == roleId);

        return Result.Success();
    }

    /// <summary>
    /// Records that someone tried to register this address again. Nothing changes; the event lets a handler tell the owner, so the
    /// registration response stays identical for known and unknown addresses.
    /// </summary>
    public void NoteRegistrationAttempt() => Raise(new RegistrationAttemptedDomainEvent(Id, Email, Locale));

    private static string NewSecurityStamp() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private void ResetFailures()
    {
        AccessFailedCount = 0;
        LockoutEnd = null;
    }
}
