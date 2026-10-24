using Microsoft.EntityFrameworkCore;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Sessions;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class SessionRepository(AuthDbContext context) : ISessionRepository
{
    private const string DeviceCollation = "Latin1_General_100_BIN2";

    public Task<UserSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Sessions().SingleOrDefaultAsync(session => session.Id == id, cancellationToken);

    // The token only selects the session; the Include is unfiltered, so the whole chain is loaded (Ruling R7).
    public Task<UserSession?> GetByRefreshTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken)
        => Sessions().SingleOrDefaultAsync(
            session => session.RefreshTokens.Any(token => token.TokenHash == tokenHash),
            cancellationToken);

    public async Task<IReadOnlyList<UserSession>> GetActiveByUserAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
        => await Sessions()
            .Where(session => session.UserId == userId && session.RevokedAt == null && session.ExpiresAt > now)
            .OrderBy(session => session.CreatedAt)
            .ToListAsync(cancellationToken);

    // One aggregate over the user's sessions. The pair is compared with a binary collation, because the column collation is case and
    // accent insensitive and a device is told apart exactly; a null agent compares equal to a null agent (EF's null semantics).
    public async Task<DeviceFamiliarity> GetDeviceFamiliarityAsync(Guid userId, string deviceName, string? userAgent, CancellationToken cancellationToken)
    {
        var counts = await context.Set<UserSession>()
            .AsNoTracking()
            .Where(session => session.UserId == userId)
            .GroupBy(session => session.UserId)
            .Select(group => new
            {
                Matching = group.Count(session =>
                    EF.Functions.Collate(session.DeviceName, DeviceCollation) == deviceName
                    && (userAgent == null
                        ? session.UserAgent == null
                        : EF.Functions.Collate(session.UserAgent!, DeviceCollation) == userAgent)),
            })
            .SingleOrDefaultAsync(cancellationToken);

        return counts switch
        {
            null => DeviceFamiliarity.NoStoredSession,
            { Matching: > 0 } => DeviceFamiliarity.Known,
            _ => DeviceFamiliarity.New,
        };
    }

    public void Add(UserSession session) => context.Set<UserSession>().Add(session);

    // One UPDATE ... WHERE UsedAt IS NULL AND RevokedAt IS NULL: the database decides which concurrent caller wins. ExecuteUpdate
    // bypasses the change tracker, so a session this context already holds keeps its in-memory state.
    public async Task<bool> TryClaimRefreshTokenAsync(Guid refreshTokenId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var claimed = await context.Set<RefreshToken>()
            .Where(token => token.Id == refreshTokenId && token.UsedAt == null && token.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.UsedAt, now), cancellationToken);

        return claimed == 1;
    }

    // A projection, so nothing is tracked and the loaded session (with this token in its chain) is not refreshed from the database.
    public Task<RefreshTokenState?> GetRefreshTokenStateAsync(Guid refreshTokenId, CancellationToken cancellationToken)
        => context.Set<RefreshToken>()
            .AsNoTracking()
            .Where(token => token.Id == refreshTokenId)
            .Select(token => new RefreshTokenState(token.UsedAt, token.RevokedAt))
            .SingleOrDefaultAsync(cancellationToken);

    private IQueryable<UserSession> Sessions() => context.Set<UserSession>().Include(session => session.RefreshTokens);
}
