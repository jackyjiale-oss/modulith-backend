using Microsoft.EntityFrameworkCore;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Roles;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class PermissionRepository(AuthDbContext context) : IPermissionRepository
{
    public async Task<IReadOnlyList<Permission>> ListAsync(CancellationToken cancellationToken)
        => await context.Set<Permission>().ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Permission>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var distinctIds = ids.Distinct().ToList();

        return await context.Set<Permission>().Where(permission => distinctIds.Contains(permission.Id)).ToListAsync(cancellationToken);
    }

    public void Add(Permission permission) => context.Set<Permission>().Add(permission);
}
