using Microsoft.EntityFrameworkCore;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Verification;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class PhoneVerificationCodeRepository(AuthDbContext context) : IPhoneVerificationCodeRepository
{
    public void Add(PhoneVerificationCode code) => context.Set<PhoneVerificationCode>().Add(code);

    public Task<PhoneVerificationCode?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => context.Set<PhoneVerificationCode>().SingleOrDefaultAsync(code => code.Id == id, cancellationToken);

    public Task<PhoneVerificationCode?> GetPendingAsync(
        Guid userId,
        PhoneCodePurpose purpose,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => Pending(userId, now)
            .Where(code => code.Purpose == purpose)
            .OrderByDescending(code => code.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<PhoneVerificationCode>> ListPendingAsync(
        Guid userId,
        PhoneCodePurpose? purpose,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => await Pending(userId, now)
            .Where(code => purpose == null || code.Purpose == purpose)
            .ToListAsync(cancellationToken);

    public Task<DateTimeOffset?> GetLastIssuedAtAsync(Guid userId, CancellationToken cancellationToken)
        => context.Set<PhoneVerificationCode>()
            .Where(code => code.UserId == userId)
            .OrderByDescending(code => code.CreatedAt)
            .Select(code => (DateTimeOffset?)code.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<DateTimeOffset>> ListIssuedSinceAsync(Guid userId, DateTimeOffset since, CancellationToken cancellationToken)
        => await context.Set<PhoneVerificationCode>()
            .Where(code => code.UserId == userId && code.CreatedAt >= since)
            .OrderBy(code => code.CreatedAt)
            .Select(code => code.CreatedAt)
            .ToListAsync(cancellationToken);

    // One UPDATE ... WHERE: a try is claimed only while fewer than maxAttempts were, so concurrent guesses queue on the row lock and at
    // most maxAttempts of them ever compare a code.
    public async Task<bool> TryClaimAttemptAsync(Guid id, int maxAttempts, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var claimed = await context.Set<PhoneVerificationCode>()
            .Where(code => code.Id == id
                && code.AttemptCount < maxAttempts
                && code.ConsumedAt == null
                && code.InvalidatedAt == null
                && code.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(code => code.AttemptCount, code => code.AttemptCount + 1), cancellationToken);
        return claimed == 1;
    }

    // The rule of PhoneVerificationCode.IsPending in one UPDATE ... WHERE: the database decides which concurrent caller wins.
    public async Task<bool> TryConsumeAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var consumed = await context.Set<PhoneVerificationCode>()
            .Where(code => code.Id == id && code.ConsumedAt == null && code.InvalidatedAt == null && code.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(code => code.ConsumedAt, now), cancellationToken);

        return consumed == 1;
    }

    // The same rule as PhoneVerificationCode.IsPending: neither consumed nor invalidated, and now < ExpiresAt.
    private IQueryable<PhoneVerificationCode> Pending(Guid userId, DateTimeOffset now)
        => context.Set<PhoneVerificationCode>()
            .Where(code => code.UserId == userId && code.ConsumedAt == null && code.InvalidatedAt == null && code.ExpiresAt > now);
}
