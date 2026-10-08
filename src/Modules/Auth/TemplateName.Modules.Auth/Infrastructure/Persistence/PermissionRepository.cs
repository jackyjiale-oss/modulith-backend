using Microsoft.EntityFrameworkCore;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Roles;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class PermissionRepository(AuthDbContext context) : IPermissionRepository
{
    public async Task<IReadOnlyList<Permission>> ListAsync(CancellationToken cancellationToken)
        => await context.Set<Permission>().ToListAsync(cancellationToken);

    public Task<int> CountExistingAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var distinctIds = ids.Distinct().ToList();

        return context.Set<Permission>().CountAsync(permission => distinctIds.Contains(permission.Id), cancellationToken);
    }

    public void Add(Permission permission) => context.Set<Permission>().Add(permission);
}
