using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Application.Admin.Users.List;

/// <summary>A user as the administration list shows it. Never a password hash, a security stamp or any token.</summary>
/// <param name="Id">The user id.</param>
/// <param name="Email">The address as it was registered (trimmed).</param>
/// <param name="DisplayName">The name to show.</param>
/// <param name="Status">Whether the account is active or suspended (locked by an administrator).</param>
/// <param name="EmailConfirmed">Whether the address has been confirmed.</param>
/// <param name="IsLockedOut">Whether failed sign-ins have locked the account right now.</param>
/// <param name="CreatedAt">When the account was created (UTC).</param>
/// <param name="LastLoginAt">The last successful sign-in (UTC), if any.</param>
internal sealed record UserListItemResponse(
    Guid Id,
    string Email,
    string DisplayName,
    UserStatus Status,
    bool EmailConfirmed,
    bool IsLockedOut,
    DateTime CreatedAt,
    DateTime? LastLoginAt);
