using TemplateName.Modules.Auth.Domain.Mfa;

namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>Loads, adds and removes a user's <see cref="RecoveryCode"/>s, and uses one in one atomic statement.</summary>
internal interface IRecoveryCodeRepository
{
    /// <summary>Every code of the user, used or not, tracked, so the caller can remove the whole set before issuing a new one.</summary>
    Task<IReadOnlyList<RecoveryCode>> ListByUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The user's unused code with this hash (<c>RecoveryCodeFormat.Hash</c> of the normalized input); null when none matches.</summary>
    Task<RecoveryCode?> FindUnusedAsync(Guid userId, byte[] codeHash, CancellationToken cancellationToken);

    /// <summary>How many of the user's codes are still unused.</summary>
    Task<int> CountUnusedAsync(Guid userId, CancellationToken cancellationToken);

    void Add(RecoveryCode code);

    void Remove(RecoveryCode code);

    /// <summary>
    /// Marks the code used in one atomic statement, outside the change tracker (<c>UPDATE ... SET UsedAt = @now WHERE Id = @id AND
    /// UsedAt IS NULL</c>). True only for the one caller whose statement changed the row; false when the code is unknown or already used,
    /// so two requests with one code can never both succeed.
    /// </summary>
    Task<bool> TryUseAsync(Guid codeId, DateTimeOffset now, CancellationToken cancellationToken);
}
