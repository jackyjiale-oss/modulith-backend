namespace TemplateName.Modules.Auth.Contracts.IntegrationEvents;

/// <summary>Why a password-reset link was issued, so the message can be worded for the situation.</summary>
public enum PasswordResetReason
{
    /// <summary>The user asked for it ("forgot password").</summary>
    SelfService = 0,

    /// <summary>An administrator created the account without a password; the link lets the user set the first one.</summary>
    CreatedByAdmin = 1,

    /// <summary>An administrator forced a reset of an existing account's password.</summary>
    ForcedByAdmin = 2,
}
