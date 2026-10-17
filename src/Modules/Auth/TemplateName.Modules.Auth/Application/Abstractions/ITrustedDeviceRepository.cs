using TemplateName.Modules.Auth.Domain.TrustedDevices;

namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>Loads and adds <see cref="TrustedDevice"/> aggregates; a device changes (used, revoked) through the change tracker.</summary>
internal interface ITrustedDeviceRepository
{
    /// <summary>Finds a device by the SHA-256 hash of its token, whatever its state.</summary>
    Task<TrustedDevice?> GetByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken);

    /// <summary>The device with <paramref name="id"/> when it belongs to <paramref name="userId"/>; null otherwise, so another user's id is "not found".</summary>
    Task<TrustedDevice?> GetForUserAsync(Guid id, Guid userId, CancellationToken cancellationToken);

    void Add(TrustedDevice device);
}
