using Microsoft.EntityFrameworkCore;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class UserRepository(AuthDbContext context) : IUserRepository
{
    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Users().SingleOrDefaultAsync(user => user.Id == id, cancellationToken);

    public Task<User?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
        => Users().SingleOrDefaultAsync(user => user.NormalizedEmail == normalizedEmail, cancellationToken);

    public Task<bool> ExistsByNormalizedEmailIncludingDeletedAsync(string normalizedEmail, CancellationToken cancellationToken)
        => context.Set<User>()
            .IgnoreQueryFilters([ModelConventions.SoftDeleteFilterName])
            .AnyAsync(user => user.NormalizedEmail == normalizedEmail, cancellationToken);

    public void Add(User user) => context.Set<User>().Add(user);

    // The whole aggregate. The history is ordered oldest first, because ChangePassword trims it from the front.
    private IQueryable<User> Users() => context.Set<User>()
        .Include(user => user.PasswordHistory.OrderBy(entry => entry.CreatedAt).ThenBy(entry => entry.Id))
        .Include(user => user.Roles)
        .AsSingleQuery();
}
