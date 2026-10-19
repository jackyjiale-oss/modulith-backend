namespace TemplateName.Modules.Auth.Application.Mfa;

/// <summary>
/// The second factor a caller presents: a code from the authenticator app or one of the recovery codes, exactly one of them. The values
/// are what the caller typed; <see cref="MfaFactorVerifier"/> checks the shape.
/// </summary>
/// <param name="Code">A 6-digit TOTP code, or null.</param>
/// <param name="RecoveryCode">A recovery code in any spelling the normalization accepts, or null.</param>
internal sealed record MfaProof(string? Code, string? RecoveryCode)
{
    /// <summary>Only the type name, so a logged or formatted proof can never carry a code.</summary>
    public override string ToString() => nameof(MfaProof);
}
