using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Application.Admin.Users.Get;

/// <summary>A user as an administrator sees it. Never a password hash, a security stamp, the password history or any token.</summary>
/// <param name="Id">The user id.</param>
/// <param name="Email">The address as it was registered (trimmed).</param>
/// <param name="EmailConfirmed">Whether the address has been confirmed.</param>
/// <param name="DisplayName">The name to show.</param>
/// <param name="Locale">The saved language.</param>
/// <param name="TimeZone">The saved time zone (IANA id).</param>
/// <param name="Status">Whether the account is active or suspended (locked by an administrator).</param>
/// <param name="HasPassword">False for an account an administrator created until its user sets a password through the reset link.</param>
/// <param name="IsLockedOut">Whether failed sign-ins have locked the account right now.</param>
/// <param name="LockoutEnd">When that login lockout ends, if one was set.</param>
/// <param name="LastLoginAt">The last successful sign-in, if any.</param>
/// <param name="PasswordChangedAt">When the password was last set, if ever.</param>
/// <param name="CreatedAt">When the account was created (UTC).</param>
/// <param name="Roles">The user's roles that are not deleted, sorted by name.</param>
internal sealed record UserResponse(
    Guid Id,
    string Email,
    bool EmailConfirmed,
    string DisplayName,
    string Locale,
    string TimeZone,
    UserStatus Status,
    bool HasPassword,
    bool IsLockedOut,
    DateTimeOffset? LockoutEnd,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset? PasswordChangedAt,
    DateTime CreatedAt,
    IReadOnlyList<UserRoleResponse> Roles);
