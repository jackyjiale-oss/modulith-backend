namespace TemplateName.Modules.Auth.Application.Mfa;

/// <summary>
/// A new set of recovery codes, shown this once: only their hashes are stored, so they can never be shown again. The client asks the user
/// to keep them somewhere safe; each signs in once when the authenticator app is not at hand.
/// </summary>
/// <param name="RecoveryCodes">The ten codes as shown to the user (<c>XXXX-XXXX-XXXX-XXXX</c>); case, hyphens and spaces do not matter when one is typed.</param>
internal sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes)
{
    /// <summary>Only the type name, so a logged response can never carry a code.</summary>
    public override string ToString() => nameof(RecoveryCodesResponse);
}
