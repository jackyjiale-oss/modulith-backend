namespace TemplateName.Modules.Auth.Application.Mfa.SetUpTotp;

/// <summary>
/// The secret of a new authenticator setup, shown this once: the server keeps only its protected form and never shows it again (a lost
/// QR code means starting the setup again). The client draws <see cref="OtpAuthUri"/> as a QR code and offers <see cref="Secret"/> for
/// typing; then it confirms the setup with a code from the app.
/// </summary>
/// <param name="MethodId">The pending method.</param>
/// <param name="Secret">The TOTP secret in unpadded base32, for typing into the app.</param>
/// <param name="OtpAuthUri">The <c>otpauth://totp/</c> URI with the issuer, the account's email and the secret, for the QR code.</param>
internal sealed record TotpSetupResponse(Guid MethodId, string Secret, string OtpAuthUri)
{
    /// <summary>The type name and the method id only, so a logged response can never carry the secret.</summary>
    public override string ToString() => $"{nameof(TotpSetupResponse)} {{ {nameof(MethodId)} = {MethodId} }}";
}
