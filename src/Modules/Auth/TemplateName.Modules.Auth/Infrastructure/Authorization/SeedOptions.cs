namespace TemplateName.Modules.Auth.Infrastructure.Authorization;

/// <summary>
/// What the Auth seeder does (section <c>Auth:Seed</c>). The administrator account is created only when both
/// <see cref="AdminEmail"/> and <see cref="AdminPassword"/> are set; the password comes from user secrets or the environment, never a file.
/// </summary>
internal sealed class SeedOptions
{
    internal const string SectionName = "Auth:Seed";

    /// <summary>Whether the host seeds when it starts, after the migration step. The host reads this key itself.</summary>
    public bool RunOnStartup { get; set; } = true;

    /// <summary>The email address of the first administrator (a <c>SuperAdmin</c>). Empty: no administrator is seeded.</summary>
    public string? AdminEmail { get; set; }

    /// <summary>The first administrator's password, within the <c>Auth:Password</c> length limits. Empty: no administrator is seeded.</summary>
    public string? AdminPassword { get; set; }
}
