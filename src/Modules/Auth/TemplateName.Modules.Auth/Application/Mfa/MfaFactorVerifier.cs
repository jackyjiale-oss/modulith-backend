using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Mfa;

namespace TemplateName.Modules.Auth.Application.Mfa;

/// <summary>
/// Checks a second factor of a user and, when it is right, spends it in the database: a TOTP code claims its time step (so the same
/// code, or an older one, never works again), a recovery code is marked used. The factor counts only when that claim wins, so of two
/// simultaneous requests with one code exactly one succeeds. It never counts failures: the caller decides what a wrong code costs
/// (the lockout counter, a challenge's attempts). Nothing it logs carries a code, a recovery code or the secret.
/// </summary>
internal sealed partial class MfaFactorVerifier(
    IMfaMethodRepository methods,
    IRecoveryCodeRepository recoveryCodes,
    IMfaSecretProtector secretProtector,
    ILogger<MfaFactorVerifier> logger)
{
    /// <summary>
    /// The factor <paramref name="proof"/> proves for <paramref name="userId"/> at <paramref name="now"/>, already spent; null when it
    /// is wrong, reused, unreadable or malformed, when the user has no confirmed authenticator, when the claim was lost to another
    /// request, and when the proof holds both values or neither. The claims ignore <paramref name="cancellationToken"/>: once one may
    /// have committed, the caller must be able to store the outcome.
    /// </summary>
    public async Task<MfaFactor?> VerifyAsync(Guid userId, MfaProof proof, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proof);

        return proof switch
        {
            { Code: { } code, RecoveryCode: null } => await VerifyTotpAsync(userId, code, now, cancellationToken),
            { Code: null, RecoveryCode: { } recoveryCode } => await VerifyRecoveryCodeAsync(userId, recoveryCode, now, cancellationToken),
            _ => null,
        };
    }

    private async Task<MfaFactor?> VerifyTotpAsync(Guid userId, string code, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var method = await methods.GetTotpAsync(userId, cancellationToken);
        if (method is not { IsConfirmed: true })
        {
            return null;
        }

        byte[] secret;
        try
        {
            secret = secretProtector.Unprotect(method.SecretEncrypted);
        }
        catch (CryptographicException)
        {
            // The key ring lost the key, or the value was changed. The exception says nothing the operator needs beyond the id, and
            // nothing about the value is logged; the user signs in with a recovery code until the authenticator is set up again.
            LogUnreadableSecret(logger, method.Id);
            return null;
        }

        try
        {
            var step = Totp.FindMatchingStep(secret, code, now, method.LastUsedTimeStep);
            if (step is not { } matched)
            {
                return null;
            }

            return await methods.TryClaimTimeStepAsync(method.Id, matched, now, CancellationToken.None) ? MfaFactor.Totp : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private async Task<MfaFactor?> VerifyRecoveryCodeAsync(Guid userId, string recoveryCode, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (RecoveryCodeFormat.Normalize(recoveryCode) is not { } normalized)
        {
            return null;
        }

        var stored = await recoveryCodes.FindUnusedAsync(userId, RecoveryCodeFormat.Hash(normalized), cancellationToken);
        if (stored is null)
        {
            return null;
        }

        return await recoveryCodes.TryUseAsync(stored.Id, now, CancellationToken.None) ? MfaFactor.RecoveryCode : null;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not read the TOTP secret of MFA method {MethodId}")]
    private static partial void LogUnreadableSecret(ILogger logger, Guid methodId);
}
