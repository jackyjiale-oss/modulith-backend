using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Application.Passwords;

internal static class PasswordHasherExtensions
{
    /// <summary>
    /// Whether <paramref name="password"/> matches one of the user's recent passwords: every entry of
    /// <see cref="User.PasswordHistory"/>, whose newest entry is the current hash, so the reuse rule covers the current password and the
    /// <c>Auth:Password:HistoryCount</c> minus one before it. Every entry is verified, with no early exit, so the time taken does not tell
    /// which one matched. A user without a password has no history and reuses nothing.
    /// </summary>
    public static bool IsRecentPassword(this IPasswordHasher passwordHasher, User user, string password)
    {
        var isReused = false;
        foreach (var entry in user.PasswordHistory)
        {
            isReused |= passwordHasher.Verify(entry.PasswordHash, password) != PasswordVerification.Failed;
        }

        return isReused;
    }
}
