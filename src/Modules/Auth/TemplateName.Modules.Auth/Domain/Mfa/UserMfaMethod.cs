using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Mfa;

/// <summary>
/// A second factor of one user: today one authenticator app (TOTP). It is pending from <see cref="StartTotp"/> until
/// <see cref="Confirm"/> proves the user can produce a code, and only a confirmed method turns two-step verification on. The secret is
/// stored encrypted (<see cref="SecretEncrypted"/> is the output of the Auth secret protector, never the secret itself) and is never
/// raised in an event. <see cref="LastUsedTimeStep"/> is the replay guard: the repository advances it with one conditional update
/// (<c>IMfaMethodRepository.TryClaimTimeStepAsync</c>), so the aggregate never compares steps itself.
/// </summary>
internal sealed class UserMfaMethod : AggregateRoot<Guid>
{
    /// <summary>The column limit of <see cref="Label"/>; the validators refuse a longer label.</summary>
    public const int MaxLabelLength = 100;

    /// <summary>The column limit of <see cref="SecretEncrypted"/>.</summary>
    public const int MaxSecretEncryptedLength = 512;

    // EF Core materializes the aggregate through this constructor; callers use StartTotp.
    private UserMfaMethod()
    {
    }

    public Guid UserId { get; private set; }

    public MfaMethodType Type { get; private set; }

    /// <summary>A name the user recognizes, such as the app or the phone.</summary>
    public string Label { get; private set; } = string.Empty;

    /// <summary>The secret as protected by the Auth secret protector. Never the plain secret.</summary>
    public string SecretEncrypted { get; private set; } = string.Empty;

    /// <summary>When the setup started or, after <see cref="RestartSetup"/>, restarted.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ConfirmedAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    /// <summary>The TOTP time step of the last accepted code. A code counts only for a greater step.</summary>
    public long? LastUsedTimeStep { get; private set; }

    /// <summary>True once <see cref="Confirm"/> succeeded; until then the method is pending and protects nothing.</summary>
    public bool IsConfirmed => ConfirmedAt is not null;

    /// <summary>Starts the setup of an authenticator app. The method is pending until <see cref="Confirm"/>.</summary>
    /// <exception cref="ArgumentException">
    /// The label or secret is blank or longer than its column. The application validator refuses such a label first and the secret
    /// comes from the protector, so either is a programming error here.
    /// </exception>
    public static UserMfaMethod StartTotp(Guid userId, string label, string secretEncrypted, DateTimeOffset now)
    {
        var method = new UserMfaMethod
        {
            Id = SequentialGuid.Create(now),
            UserId = userId,
            Type = MfaMethodType.Totp,
        };
        method.SetUp(label, secretEncrypted, now);

        return method;
    }

    /// <summary>
    /// Replaces the secret of a setup that was not confirmed, for a user who starts again (a lost QR code). <see cref="CreatedAt"/>
    /// restarts, so a clean-up of abandoned setups counts from the latest attempt.
    /// </summary>
    /// <exception cref="InvalidOperationException">The method is already confirmed: replacing an authenticator means removing it first.</exception>
    /// <exception cref="ArgumentException">The label or secret is blank or longer than its column.</exception>
    public void RestartSetup(string label, string secretEncrypted, DateTimeOffset now)
    {
        if (IsConfirmed)
        {
            throw new InvalidOperationException("A confirmed method cannot be set up again; remove it first.");
        }

        SetUp(label, secretEncrypted, now);
    }

    /// <summary>
    /// Turns a pending method into a confirmed one after the caller verified a code for <paramref name="timeStep"/>; that step becomes
    /// <see cref="LastUsedTimeStep"/>, so the same code cannot be used again right away.
    /// </summary>
    /// <returns>Success, or <see cref="MfaErrors.AlreadyEnabled"/> when the method is already confirmed.</returns>
    public Result Confirm(long timeStep, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeStep);
        if (IsConfirmed)
        {
            return Result.Failure(MfaErrors.AlreadyEnabled);
        }

        ConfirmedAt = now;
        LastUsedAt = now;
        LastUsedTimeStep = timeStep;

        return Result.Success();
    }

    private void SetUp(string label, string secretEncrypted, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretEncrypted);
        if (label.Length > MaxLabelLength)
        {
            throw new ArgumentException($"A label has at most {MaxLabelLength} characters.", nameof(label));
        }

        if (secretEncrypted.Length > MaxSecretEncryptedLength)
        {
            throw new ArgumentException($"A protected secret has at most {MaxSecretEncryptedLength} characters.", nameof(secretEncrypted));
        }

        Label = label;
        SecretEncrypted = secretEncrypted;
        CreatedAt = now;
    }
}
