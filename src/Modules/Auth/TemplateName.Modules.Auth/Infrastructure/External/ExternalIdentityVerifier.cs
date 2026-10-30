using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Infrastructure.External;

/// <summary>
/// The one entry point for verifying a provider's proof: exactly one of an ID token and an access token. An ID token goes to
/// <see cref="OidcIdTokenVerifier"/> for every provider (Facebook's iOS Limited Login token included), and its nonce is spent through
/// <see cref="IExternalProofReplayGuard"/> only after every check passed. An access token is Facebook's (Task 6 adds that verifier).
/// </summary>
internal sealed class ExternalIdentityVerifier(OidcIdTokenVerifier idTokens, IExternalProofReplayGuard replayGuard) : IExternalIdentityVerifier
{
    public async Task<Result<VerifiedExternalIdentity>> VerifyAsync(ExternalProvider provider, ExternalProof proof, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(proof);

        var hasIdToken = !string.IsNullOrEmpty(proof.IdToken);
        var hasAccessToken = !string.IsNullOrEmpty(proof.AccessToken);
        if (hasIdToken == hasAccessToken)
        {
            return ExternalLoginErrors.InvalidToken;
        }

        if (hasAccessToken)
        {
            return await VerifyAccessTokenAsync(provider, proof.AccessToken!, cancellationToken);
        }

        var verified = await idTokens.VerifyAsync(provider, proof.IdToken!, proof.Nonce, cancellationToken);
        if (verified.IsFailure)
        {
            return verified.Error;
        }

        // Spent last, so a refused proof never burns its nonce, and under the token's own nonce claim: a replay of this token carries that
        // value even when it posts the hashed form as if it were the raw nonce (the hash then matches itself).
        return await replayGuard.TryClaimAsync(provider.Name, verified.Value.Nonce, cancellationToken)
            ? verified.Value.Identity
            : ExternalLoginErrors.InvalidToken;
    }

    // SEAM (Task 6): a Facebook access token is verified by FacebookAccessTokenVerifier (debug_token and /me on the Graph API). Until then,
    // and for every provider that is not Facebook, an access token is not a proof.
    private static Task<Result<VerifiedExternalIdentity>> VerifyAccessTokenAsync(ExternalProvider provider, string accessToken, CancellationToken cancellationToken)
    {
        _ = provider;
        _ = accessToken;
        _ = cancellationToken;
        return Task.FromResult<Result<VerifiedExternalIdentity>>(ExternalLoginErrors.InvalidToken);
    }
}
