using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>
/// What a client posts to prove an identity at a provider: an OpenID Connect ID token with the raw nonce it asked the provider to bind,
/// or (Facebook only) an access token. The record prints only its type name (<see cref="ToString"/>), so a proof never reaches a log.
/// </summary>
internal sealed record ExternalProof(string? IdToken, string? AccessToken, string? Nonce)
{
    /// <summary>The longest <see cref="IdToken"/> or <see cref="AccessToken"/> accepted; a longer one is refused unread.</summary>
    public const int MaxTokenLength = 8192;

    /// <summary>The shortest <see cref="Nonce"/> accepted.</summary>
    public const int MinNonceLength = 16;

    /// <summary>The longest <see cref="Nonce"/> accepted.</summary>
    public const int MaxNonceLength = 256;

    /// <summary>The type name only: the generated <c>ToString</c> would print the token and the nonce.</summary>
    public override string ToString() => nameof(ExternalProof);
}

/// <summary>
/// An identity a provider vouched for. <paramref name="Provider"/> is the provider's stored (lower-case) name and <paramref name="Subject"/>
/// its stable identifier of the person (at most 256 characters; Microsoft's is <c>{tid}:{oid}</c>). <paramref name="Email"/> is the token's
/// email for display (null when absent, blank or over 256 characters); <paramref name="HasUsableEmail"/> says whether it may find or link an
/// account (the provider's email rule holds and its <c>TrustEmail</c> is on). The record prints only the provider, never the email.
/// </summary>
internal sealed record VerifiedExternalIdentity(string Provider, string Subject, string? Email, bool HasUsableEmail, string? Name)
{
    /// <summary>The provider only: the generated <c>ToString</c> would print the subject and the email.</summary>
    public override string ToString() => $"{nameof(VerifiedExternalIdentity)} {{ Provider = {Provider} }}";
}

/// <summary>Turns a provider's proof into a verified identity, or says why it cannot.</summary>
internal interface IExternalIdentityVerifier
{
    /// <summary>
    /// Verifies <paramref name="proof"/> for <paramref name="provider"/> and, when it holds, spends its nonce so the same proof is refused
    /// next time. Fails with <c>ExternalLoginErrors.InvalidToken</c> for any bad proof (malformed, oversized, forged, expired, too old, wrong
    /// audience or issuer, nonce missing, different or already used) and <c>ExternalLoginErrors.ProviderUnavailable</c> when the provider's
    /// metadata cannot be fetched. A failure never spends the nonce, and no input makes it throw (a cancelled caller still does).
    /// </summary>
    Task<Result<VerifiedExternalIdentity>> VerifyAsync(ExternalProvider provider, ExternalProof proof, CancellationToken cancellationToken);
}
