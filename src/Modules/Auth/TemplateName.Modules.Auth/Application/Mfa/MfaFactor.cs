namespace TemplateName.Modules.Auth.Application.Mfa;

/// <summary>The second factor <see cref="MfaFactorVerifier"/> accepted.</summary>
internal enum MfaFactor
{
    /// <summary>A code from the authenticator app; its time step is now claimed.</summary>
    Totp = 0,

    /// <summary>A recovery code; it is now used.</summary>
    RecoveryCode = 1,
}
