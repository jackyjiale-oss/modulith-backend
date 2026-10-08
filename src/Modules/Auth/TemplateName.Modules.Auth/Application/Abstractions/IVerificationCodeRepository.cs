using TemplateName.Modules.Auth.Domain.Verification;

namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>Loads and adds <see cref="VerificationCode"/> aggregates.</summary>
internal interface IVerificationCodeRepository
{
    /// <summary>Finds a code by its token hash, whatever its state.</summary>
    Task<VerificationCode?> GetByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken);

    /// <summary>The user's codes for <paramref name="purpose"/> that are still pending at <paramref name="now"/>, to invalidate them.</summary>
    Task<IReadOnlyList<VerificationCode>> GetPendingAsync(
        Guid userId,
        VerificationPurpose purpose,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>When the user's latest code for <paramref name="purpose"/> was issued (UTC), for the resend cooldown; null if none.</summary>
    Task<DateTime?> GetLastIssuedAtAsync(Guid userId, VerificationPurpose purpose, CancellationToken cancellationToken);

    void Add(VerificationCode code);
}
