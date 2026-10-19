namespace TemplateName.Modules.Auth.Application.Mfa.GetMethods;

/// <summary>The caller's two-step verification. Never a secret or a code.</summary>
/// <param name="Enabled">True when a confirmed method exists; a pending setup does not count.</param>
/// <param name="RecoveryCodesRemaining">How many of the caller's recovery codes are still unused.</param>
/// <param name="Methods">The caller's methods, pending or confirmed: at most one today (one authenticator per user), so the list is not paged.</param>
internal sealed record MfaMethodsResponse(bool Enabled, int RecoveryCodesRemaining, IReadOnlyList<MfaMethodResponse> Methods);
