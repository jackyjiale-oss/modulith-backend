using Microsoft.EntityFrameworkCore;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.TrustedDevices;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class TrustedDeviceRepository(AuthDbContext context) : ITrustedDeviceRepository
{
    public Task<TrustedDevice?> GetByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken)
        => context.Set<TrustedDevice>().SingleOrDefaultAsync(device => device.TokenHash == tokenHash, cancellationToken);

    public Task<TrustedDevice?> GetForUserAsync(Guid id, Guid userId, CancellationToken cancellationToken)
        => context.Set<TrustedDevice>().SingleOrDefaultAsync(device => device.Id == id && device.UserId == userId, cancellationToken);

    public void Add(TrustedDevice device) => context.Set<TrustedDevice>().Add(device);
}
