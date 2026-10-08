using Dapper;
using Microsoft.Extensions.Caching.Hybrid;
using TemplateName.Application.Common.Data;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Infrastructure.Authorization;

/// <summary>
/// Resolves a user's permission codes on the server (ADR 0016): one Dapper query on a cache miss, then the set from
/// <see cref="HybridCache"/> under <c>perm:{userId}</c> for <see cref="PermissionCache.Lifetime"/>. An unknown, suspended or
/// soft-deleted user has the empty set, which is cached like any other. Codes are compared exactly (ordinal).
/// </summary>
internal sealed class PermissionChecker(IDbConnectionFactory connectionFactory, HybridCache cache) : IPermissionChecker, IPermissionCache
{
    // Dapper bypasses the EF Core soft-delete filters, so the query repeats them for users and roles (ADR 0006). Assignments and grants
    // have no flags of their own: they are hidden through their owners. A deprecated permission is never granted, even by an old grant.
    private const string Sql = """
        SELECT DISTINCT p.Code
        FROM auth.Users AS u
        INNER JOIN auth.UserRoles AS ur ON ur.UserId = u.Id
        INNER JOIN auth.Roles AS r ON r.Id = ur.RoleId
        INNER JOIN auth.RolePermissions AS rp ON rp.RoleId = r.Id
        INNER JOIN auth.Permissions AS p ON p.Id = rp.PermissionId
        WHERE u.Id = @UserId
          AND u.IsDeleted = 0
          AND u.Status = @ActiveStatus
          AND r.IsDeleted = 0
          AND p.IsDeprecated = 0
        """;

    public async Task<bool> HasPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(permission);

        var codes = await cache.GetOrCreateAsync(
            PermissionCache.Key(userId),
            (ConnectionFactory: connectionFactory, UserId: userId),
            static (state, cancellationToken) => LoadAsync(state.ConnectionFactory, state.UserId, cancellationToken),
            PermissionCache.EntryOptions,
            cancellationToken: cancellationToken);

        // The set is sorted ordinally when it is loaded, so membership is a binary search.
        return codes is not null && Array.BinarySearch(codes, permission, StringComparer.Ordinal) >= 0;
    }

    public async Task InvalidateUsersAsync(IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        foreach (var userId in userIds.Distinct())
        {
            await cache.RemoveAsync(PermissionCache.Key(userId), cancellationToken);
        }
    }

    // A string array, so HybridCache can serialize it when a distributed cache is added (Plan 5).
    private static async ValueTask<string[]> LoadAsync(IDbConnectionFactory connectionFactory, Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var codes = await connection.QueryAsync<string>(new CommandDefinition(
            Sql,
            new { UserId = userId, ActiveStatus = (int)UserStatus.Active },
            cancellationToken: cancellationToken));

        var sorted = codes.ToArray();
        Array.Sort(sorted, StringComparer.Ordinal);
        return sorted;
    }
}
