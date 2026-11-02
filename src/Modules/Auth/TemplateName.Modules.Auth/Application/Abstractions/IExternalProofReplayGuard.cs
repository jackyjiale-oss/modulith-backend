namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>
/// Remembers the nonces of accepted provider proofs, so a stolen ID token cannot be replayed while it is young enough to pass
/// <c>Auth:External:MaxTokenAge</c> (spec D9). Best effort: shared through Redis when the host has it, per instance otherwise.
/// </summary>
internal interface IExternalProofReplayGuard
{
    /// <summary>
    /// Claims <paramref name="nonce"/> for <paramref name="provider"/>: true the first time, false while an earlier claim of the same pair
    /// is remembered. Call it only after every other check of the proof passed, so a refused proof never spends its nonce.
    /// </summary>
    Task<bool> TryClaimAsync(string provider, string nonce, CancellationToken cancellationToken);
}
