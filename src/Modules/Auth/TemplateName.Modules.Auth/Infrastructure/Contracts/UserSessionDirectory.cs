using Dapper;
using TemplateName.Application.Common.Data;
using TemplateName.Modules.Auth.Contracts.Users;

namespace TemplateName.Modules.Auth.Infrastructure.Contracts;

/// <summary>
/// <see cref="IUserSessionDirectory"/> over <c>auth.UserSessions</c>: the rule of <c>UserSession.IsActive</c> (not revoked, before its
/// expiry) in one query, at the clock's current instant.
/// </summary>
internal sealed class UserSessionDirectory(IDbConnectionFactory connectionFactory, TimeProvider timeProvider) : IUserSessionDirectory
{
    private const string Sql = """
        SELECT Id
        FROM auth.UserSessions
        WHERE UserId = @UserId AND Id IN @SessionIds AND RevokedAt IS NULL AND ExpiresAt > @Now;
        """;

    public async Task<IReadOnlySet<Guid>> GetActiveSessionIdsAsync(
        Guid userId,
        IReadOnlyCollection<Guid> sessionIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        if (sessionIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var active = await connection.QueryAsync<Guid>(new CommandDefinition(
            Sql,
            new { UserId = userId, SessionIds = sessionIds.Distinct().ToArray(), Now = timeProvider.GetUtcNow() },
            cancellationToken: cancellationToken));
        return active.ToHashSet();
    }
}
