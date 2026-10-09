using Microsoft.EntityFrameworkCore;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Verification;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class VerificationCodeRepository(AuthDbContext context) : IVerificationCodeRepository
{
    public Task<VerificationCode?> GetByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken)
        => context.Set<VerificationCode>().SingleOrDefaultAsync(code => code.TokenHash == tokenHash, cancellationToken);

    // The same rule as VerificationCode.IsPending: neither consumed nor invalidated, and now < ExpiresAt.
    public async Task<IReadOnlyList<VerificationCode>> GetPendingAsync(
        Guid userId,
        VerificationPurpose purpose,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => await context.Set<VerificationCode>()
            .Where(code => code.UserId == userId
                && code.Purpose == purpose
                && code.ConsumedAt == null
                && code.InvalidatedAt == null
                && code.ExpiresAt > now)
            .ToListAsync(cancellationToken);

    public async Task<DateTime?> GetLastIssuedAtAsync(Guid userId, VerificationPurpose purpose, CancellationToken cancellationToken)
    {
        var lastIssuedAt = await context.Set<VerificationCode>()
            .Where(code => code.UserId == userId && code.Purpose == purpose)
            .OrderByDescending(code => code.CreatedAt)
            .Select(code => (DateTimeOffset?)code.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return lastIssuedAt?.UtcDateTime;
    }

    // One UPDATE ... WHERE (the rule of VerificationCode.CanConsume): the database decides which concurrent caller wins. ExecuteUpdate
    // bypasses the change tracker, so a code this context already holds keeps its in-memory state.
    public async Task<bool> TryConsumeAsync(Guid codeId, VerificationPurpose purpose, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var consumed = await context.Set<VerificationCode>()
            .Where(code => code.Id == codeId
                && code.Purpose == purpose
                && code.ConsumedAt == null
                && code.InvalidatedAt == null
                && code.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(code => code.ConsumedAt, now), cancellationToken);

        return consumed == 1;
    }

    public void Add(VerificationCode code) => context.Set<VerificationCode>().Add(code);
}
