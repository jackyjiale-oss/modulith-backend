using Microsoft.EntityFrameworkCore;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Mfa;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class MfaMethodRepository(AuthDbContext context) : IMfaMethodRepository
{
    public Task<UserMfaMethod?> GetTotpAsync(Guid userId, CancellationToken cancellationToken)
        => context.Set<UserMfaMethod>()
            .SingleOrDefaultAsync(method => method.UserId == userId && method.Type == MfaMethodType.Totp, cancellationToken);

    public Task<UserMfaMethod?> GetForUserAsync(Guid methodId, Guid userId, CancellationToken cancellationToken)
        => context.Set<UserMfaMethod>().SingleOrDefaultAsync(method => method.Id == methodId && method.UserId == userId, cancellationToken);

    public Task<bool> HasConfirmedAsync(Guid userId, CancellationToken cancellationToken)
        => context.Set<UserMfaMethod>().AnyAsync(method => method.UserId == userId && method.ConfirmedAt != null, cancellationToken);

    public void Add(UserMfaMethod method) => context.Set<UserMfaMethod>().Add(method);

    public void Remove(UserMfaMethod method) => context.Set<UserMfaMethod>().Remove(method);

    // One UPDATE ... WHERE: the database decides which of two simultaneous verifies of one code wins (the loser's statement finds the
    // step already taken once the row lock is free). ExecuteUpdate bypasses the change tracker, so a tracked method keeps its old values.
    public async Task<bool> TryClaimTimeStepAsync(Guid methodId, long timeStep, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var claimed = await context.Set<UserMfaMethod>()
            .Where(method => method.Id == methodId
                && method.ConfirmedAt != null
                && (method.LastUsedTimeStep == null || method.LastUsedTimeStep < timeStep))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(method => method.LastUsedTimeStep, timeStep)
                    .SetProperty(method => method.LastUsedAt, now),
                cancellationToken);

        return claimed == 1;
    }
}
