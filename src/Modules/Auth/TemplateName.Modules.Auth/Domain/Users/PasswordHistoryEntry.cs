using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Users;

/// <summary>A password hash a user has used, kept so the same password is not chosen again.</summary>
internal sealed class PasswordHistoryEntry
{
    // EF Core materializes the entity through this constructor; the user creates entries.
    private PasswordHistoryEntry()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string PasswordHash { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    internal static PasswordHistoryEntry Create(Guid userId, string passwordHash, DateTimeOffset now) => new()
    {
        Id = SequentialGuid.Create(now),
        UserId = userId,
        PasswordHash = passwordHash,
        CreatedAt = now,
    };
}
