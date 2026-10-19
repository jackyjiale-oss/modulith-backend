namespace TemplateName.Modules.Auth.Contracts.IntegrationEvents;

/// <summary>
/// What happened to a user's two-step verification. Serialized by its numeric value: a new kind is appended and existing values are
/// never renumbered.
/// </summary>
public enum MfaChangeKind
{
    /// <summary>The user confirmed an authenticator and turned two-step verification on.</summary>
    Enabled = 0,

    /// <summary>The user turned two-step verification off.</summary>
    Disabled = 1,

    /// <summary>The user replaced the whole set of recovery codes.</summary>
    RecoveryCodesRegenerated = 2,

    /// <summary>An administrator removed the user's two-step verification.</summary>
    ResetByAdmin = 3,

    /// <summary>A recovery code was used to sign in (or to step up); nothing else changed. The owner should be told, in case it was not them.</summary>
    RecoveryCodeUsed = 4,
}
