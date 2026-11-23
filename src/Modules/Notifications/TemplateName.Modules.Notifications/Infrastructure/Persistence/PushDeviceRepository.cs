using Microsoft.EntityFrameworkCore;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.PushDevices;

namespace TemplateName.Modules.Notifications.Infrastructure.Persistence;

internal sealed class PushDeviceRepository(NotificationsDbContext context) : IPushDeviceRepository
{
    public Task<PushDevice?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        return context.Set<PushDevice>().SingleOrDefaultAsync(device => device.TokenHash == tokenHash, cancellationToken);
    }

    public Task<PushDevice?> GetAsync(Guid id, CancellationToken cancellationToken)
        => context.Set<PushDevice>().SingleOrDefaultAsync(device => device.Id == id, cancellationToken);

    public async Task<IReadOnlyList<PushDevice>> ListActiveAsync(Guid userId, CancellationToken cancellationToken)
        => await context.Set<PushDevice>()
            .Where(device => device.UserId == userId && device.DeactivatedAt == null)
            .OrderByDescending(device => device.LastRegisteredAt)
            .ThenByDescending(device => device.Id)
            .ToListAsync(cancellationToken);

    public void Add(PushDevice device) => context.Set<PushDevice>().Add(device);
}
