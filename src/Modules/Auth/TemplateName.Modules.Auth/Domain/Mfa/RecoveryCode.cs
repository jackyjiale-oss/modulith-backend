using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Mfa;

/// <summary>
/// One single-use recovery code of a user. Only the SHA-256 hash of the normalized code is stored (<see cref="RecoveryCodeFormat.Hash"/>);
/// the code itself is shown once, when the set is created. <see cref="UsedAt"/> is set by one conditional update
/// (<c>IRecoveryCodeRepository.TryUseAsync</c>), so two requests with one code cannot both succeed. Regenerating replaces the whole set,
/// so a code never needs a batch id.
/// </summary>
internal sealed class RecoveryCode : Entity<Guid>
{
    /// <summary>The length of <see cref="CodeHash"/> in bytes (SHA-256).</summary>
    public const int HashLength = 32;

    // EF Core materializes the entity through this constructor; callers use Create.
    private RecoveryCode()
    {
    }

    public Guid UserId { get; private set; }

    public byte[] CodeHash { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UsedAt { get; private set; }

    /// <exception cref="ArgumentException"><paramref name="codeHash"/> is not <see cref="HashLength"/> bytes.</exception>
    public static RecoveryCode Create(Guid userId, byte[] codeHash, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(codeHash);
        if (codeHash.Length != HashLength)
        {
            throw new ArgumentException($"A code hash is {HashLength} bytes.", nameof(codeHash));
        }

        return new RecoveryCode
        {
            Id = SequentialGuid.Create(now),
            UserId = userId,
            CodeHash = codeHash,
            CreatedAt = now,
        };
    }
}
