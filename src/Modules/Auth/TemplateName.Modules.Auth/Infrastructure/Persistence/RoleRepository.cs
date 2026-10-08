using Microsoft.EntityFrameworkCore;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class RoleRepository(AuthDbContext context) : IRoleRepository
{
    public Task<Role?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Roles().SingleOrDefaultAsync(role => role.Id == id, cancellationToken);

    public Task<Role?> GetByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken)
        => Roles().SingleOrDefaultAsync(role => role.NormalizedName == normalizedName, cancellationToken);

    public async Task<IReadOnlyList<Role>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
        => await Roles().Where(role => ids.Contains(role.Id)).ToListAsync(cancellationToken);

    public Task<bool> NameExistsAsync(string normalizedName, Guid? exceptId, CancellationToken cancellationToken)
        => context.Set<Role>().AnyAsync(
            role => role.NormalizedName == normalizedName && (exceptId == null || role.Id != exceptId),
            cancellationToken);

    public async Task<IReadOnlyList<Guid>> GetUserIdsInRoleAsync(Guid roleId, CancellationToken cancellationToken)
        => await context.Set<UserRole>()
            .Where(assignment => assignment.RoleId == roleId)
            .Select(assignment => assignment.UserId)
            .ToListAsync(cancellationToken);

    public void Add(Role role) => context.Set<Role>().Add(role);

    public void Remove(Role role) => context.Set<Role>().Remove(role);

    private IQueryable<Role> Roles() => context.Set<Role>().Include(role => role.Permissions);
}
