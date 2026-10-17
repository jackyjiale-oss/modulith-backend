namespace TemplateName.Modules.Auth.Domain.Mfa;

/// <summary>
/// What happened to a user's two-step verification. Carried by <c>MfaChangedDomainEvent</c> and, later, by the integration event, so it
/// is serialized by its numeric value: a new change is appended and existing values are never renumbered.
/// </summary>
internal enum MfaChange
{
    /// <summary>The user confirmed an authenticator and turned two-step verification on.</summary>
    Enabled = 0,

    /// <summary>The user turned two-step verification off.</summary>
    Disabled = 1,

    /// <summary>The user replaced the whole set of recovery codes.</summary>
    RecoveryCodesRegenerated = 2,

    /// <summary>An administrator removed the user's two-step verification.</summary>
    ResetByAdmin = 3,

    /// <summary>A recovery code was used to sign in. Nothing else changes (<c>User.NoteRecoveryCodeUsed</c>).</summary>
    RecoveryCodeUsed = 4,
}
