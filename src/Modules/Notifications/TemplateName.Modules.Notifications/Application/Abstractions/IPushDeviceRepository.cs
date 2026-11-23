using TemplateName.Modules.Notifications.Domain.PushDevices;

namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>The push devices; every device it returns is tracked, so a change is saved through <see cref="IUnitOfWork"/>.</summary>
internal interface IPushDeviceRepository
{
    /// <summary>The device registered with the token whose hash is <paramref name="tokenHash"/>, whoever owns it; null when none.</summary>
    Task<PushDevice?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken);

    /// <summary>The device, active or not; null when none.</summary>
    Task<PushDevice?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The user's active devices, the latest registration first.</summary>
    Task<IReadOnlyList<PushDevice>> ListActiveAsync(Guid userId, CancellationToken cancellationToken);

    void Add(PushDevice device);
}
