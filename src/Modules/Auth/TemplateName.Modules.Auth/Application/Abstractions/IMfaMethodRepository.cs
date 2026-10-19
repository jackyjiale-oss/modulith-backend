using TemplateName.Modules.Auth.Domain.Mfa;

namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>Loads, adds and removes <see cref="UserMfaMethod"/> aggregates, and claims TOTP time steps against replay.</summary>
internal interface IMfaMethodRepository
{
    /// <summary>The user's one TOTP method, pending or confirmed; null when there is none (the unique index allows one per type).</summary>
    Task<UserMfaMethod?> GetTotpAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The method with <paramref name="methodId"/> when it belongs to <paramref name="userId"/>; null otherwise, so another user's id is "not found".</summary>
    Task<UserMfaMethod?> GetForUserAsync(Guid methodId, Guid userId, CancellationToken cancellationToken);

    /// <summary>Whether the user has a confirmed method, that is, whether two-step verification is on. A pending setup does not count.</summary>
    Task<bool> HasConfirmedAsync(Guid userId, CancellationToken cancellationToken);

    void Add(UserMfaMethod method);

    void Remove(UserMfaMethod method);

    /// <summary>
    /// Records <paramref name="timeStep"/> as the method's last used step in one atomic statement, outside the change tracker
    /// (<c>UPDATE ... SET LastUsedTimeStep = @step, LastUsedAt = @now WHERE Id = @id AND ConfirmedAt IS NOT NULL AND (LastUsedTimeStep
    /// IS NULL OR LastUsedTimeStep &lt; @step)</c>). True only for the one caller whose statement changed the row; false when the method
    /// is unknown, still pending, or already used this step or a later one. So the same code, or an older one after a newer one, never
    /// succeeds twice, also across simultaneous requests (plan decision D3). A tracked instance keeps its old values.
    /// </summary>
    Task<bool> TryClaimTimeStepAsync(Guid methodId, long timeStep, DateTimeOffset now, CancellationToken cancellationToken);
}
