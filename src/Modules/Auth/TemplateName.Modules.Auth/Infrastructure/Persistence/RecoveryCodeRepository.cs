using Microsoft.EntityFrameworkCore;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Mfa;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class RecoveryCodeRepository(AuthDbContext context) : IRecoveryCodeRepository
{
    public async Task<IReadOnlyList<RecoveryCode>> ListByUserAsync(Guid userId, CancellationToken cancellationToken)
        => await context.Set<RecoveryCode>()
            .Where(code => code.UserId == userId)
            .OrderBy(code => code.CreatedAt)
            .ThenBy(code => code.Id)
            .ToListAsync(cancellationToken);

    public Task<RecoveryCode?> FindUnusedAsync(Guid userId, byte[] codeHash, CancellationToken cancellationToken)
        => context.Set<RecoveryCode>()
            .SingleOrDefaultAsync(code => code.UserId == userId && code.CodeHash == codeHash && code.UsedAt == null, cancellationToken);

    public Task<int> CountUnusedAsync(Guid userId, CancellationToken cancellationToken)
        => context.Set<RecoveryCode>().CountAsync(code => code.UserId == userId && code.UsedAt == null, cancellationToken);

    public void Add(RecoveryCode code) => context.Set<RecoveryCode>().Add(code);

    public void Remove(RecoveryCode code) => context.Set<RecoveryCode>().Remove(code);

    // One UPDATE ... WHERE UsedAt IS NULL: the database decides which of two requests with one code wins.
    public async Task<bool> TryUseAsync(Guid codeId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var used = await context.Set<RecoveryCode>()
            .Where(code => code.Id == codeId && code.UsedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(code => code.UsedAt, now), cancellationToken);

        return used == 1;
    }
}
