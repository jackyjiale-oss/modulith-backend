using System.Globalization;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Users;

/// <summary>
/// A sign-in identity at an external provider (Google, Apple, Microsoft or Facebook) linked to a user. The identity is the pair
/// (<see cref="Provider"/>, <see cref="Subject"/>); the email the provider gave is kept for display only and is never used to find a user.
/// A user has at most one login for each provider.
/// </summary>
internal sealed class UserExternalLogin
{
    /// <summary>The column limit of <see cref="Provider"/>.</summary>
    public const int ProviderMaxLength = 64;

    /// <summary>The column limit of <see cref="Subject"/>.</summary>
    public const int SubjectMaxLength = 256;

    /// <summary>The column limit of <see cref="Email"/>.</summary>
    public const int EmailMaxLength = 256;

    // EF Core materializes the entity through this constructor; the user creates logins.
    private UserExternalLogin()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>The provider's name, stored lower-case.</summary>
    public string Provider { get; private set; } = string.Empty;

    /// <summary>The provider's stable identifier of the person (never the email).</summary>
    public string Subject { get; private set; } = string.Empty;

    /// <summary>The email the provider gave when the login was linked; display only.</summary>
    public string? Email { get; private set; }

    public DateTimeOffset LinkedAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    /// <summary>The form a provider name is stored and compared in: lower-case, invariant.</summary>
    internal static string NormalizeProvider(string provider) => provider.ToLower(CultureInfo.InvariantCulture);

    /// <summary>Creates a login. An email longer than its column is cut to it and a blank one is dropped.</summary>
    /// <exception cref="ArgumentException">
    /// The provider or the subject is blank or longer than its column: a verified identity never is, so this is a caller's bug.
    /// </exception>
    internal static UserExternalLogin Create(Guid userId, string provider, string subject, string? email, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        if (provider.Length > ProviderMaxLength)
        {
            throw new ArgumentException($"A provider name is at most {ProviderMaxLength} characters.", nameof(provider));
        }

        if (subject.Length > SubjectMaxLength)
        {
            throw new ArgumentException($"A subject is at most {SubjectMaxLength} characters.", nameof(subject));
        }

        return new UserExternalLogin
        {
            Id = SequentialGuid.Create(now),
            UserId = userId,
            Provider = NormalizeProvider(provider),
            Subject = subject,
            Email = string.IsNullOrWhiteSpace(email) ? null : UserSession.Cut(email, EmailMaxLength),
            LinkedAt = now,
        };
    }

    /// <summary>Records a sign-in with this login.</summary>
    public void NoteUsed(DateTimeOffset now) => LastUsedAt = now;
}
