using System.Data;
using System.Globalization;
using Dapper;
using TemplateName.Application.Common.Data;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Sessions.List;

/// <summary>
/// Pages the caller's active sessions by keyset (ADR 0010). The SQL is assembled only from constants and <see cref="KeysetQuery"/>
/// clauses, whose column names come from <see cref="SessionSortFields"/>; every value is a parameter. Dapper bypasses the EF Core
/// model, so the query itself keeps to the caller's rows (<c>UserId</c>) and to the sessions that can still refresh: not revoked, not
/// past the absolute expiry, and holding a refresh token that is unused, not revoked and not past its sliding expiry. The domain's
/// <c>UserSession.IsActive</c> checks only the first two, so a session idle for longer than the sliding lifetime would still be
/// "active" there; it can no longer refresh, so the list leaves it out. A caller without a user id gets an empty page.
/// </summary>
internal sealed class ListSessionsQueryHandler(IDbConnectionFactory connectionFactory, ICurrentUser currentUser, TimeProvider timeProvider)
    : IQueryHandler<ListSessionsQuery, CursorPage<SessionResponse>>
{
    // Same boundaries as the domain: a session or token is over from its expiry instant on (now >= ExpiresAt).
    private const string Filter = """
        UserId = @UserId
        AND RevokedAt IS NULL
        AND ExpiresAt > @Now
        AND EXISTS (
            SELECT 1
            FROM [auth].[RefreshTokens] AS token
            WHERE token.SessionId = [auth].[UserSessions].Id
              AND token.UsedAt IS NULL
              AND token.RevokedAt IS NULL
              AND token.ExpiresAt > @Now)
        """;

    public async Task<Result<CursorPage<SessionResponse>>> HandleAsync(ListSessionsQuery query, CancellationToken cancellationToken)
    {
        var sortResult = SortSpecification.Parse(query.Page.Sort, SessionSortFields.Allowed, SessionSortFields.Id, SessionSortFields.Default);
        if (sortResult.IsFailure)
        {
            return sortResult.Error;
        }

        var sort = sortResult.Value;
        var userId = currentUser.UserId ?? Guid.Empty;
        var filterHash = CursorCodec.ComputeFilterHash(string.Create(CultureInfo.InvariantCulture, $"userId={userId}"));

        Cursor? cursor = null;
        if (!string.IsNullOrEmpty(query.Page.Cursor))
        {
            var cursorResult = CursorCodec.Decode(query.Page.Cursor, sort, filterHash);
            if (cursorResult.IsFailure)
            {
                return cursorResult.Error;
            }

            cursor = cursorResult.Value;
        }

        var keyset = KeysetSqlBuilder.Build(sort, cursor, query.Page.PageSize);
        var parameters = keyset.Parameters;
        parameters.Add("Take", keyset.Take);
        parameters.Add("UserId", userId, DbType.Guid);
        parameters.Add("CurrentSessionId", currentUser.SessionId, DbType.Guid);
        // datetime2 like the columns (Dapper's default DateTime is datetime, which rounds and cannot use the index as well).
        parameters.Add("Now", timeProvider.GetUtcNow().UtcDateTime, DbType.DateTime2);

        // The columns follow the SessionResponse constructor, which Dapper binds by position.
        var pageSql = $"""
            SELECT TOP (@Take) Id, DeviceName, IpAddress, UserAgent, CreatedAt, LastSeenAt, ExpiresAt,
                CAST(CASE WHEN Id = @CurrentSessionId THEN 1 ELSE 0 END AS bit) AS IsCurrent
            FROM [auth].[UserSessions]
            WHERE {Filter} AND {keyset.WhereClause}
            ORDER BY {keyset.OrderByClause}
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<SessionResponse>(new CommandDefinition(pageSql, parameters, cancellationToken: cancellationToken));

        long? totalCount = null;
        if (query.Page.IncludeTotalCount)
        {
            var countSql = $"SELECT COUNT_BIG(*) FROM [auth].[UserSessions] WHERE {Filter}";
            totalCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition(countSql, parameters, cancellationToken: cancellationToken));
        }

        // datetime2 reads back as DateTimeKind.Unspecified; the columns hold UTC. The cursor keys are taken from these rows, so they
        // carry exactly the millisecond values SQL Server stored.
        var items = rows.Select(row => row with
        {
            CreatedAt = DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc),
            LastSeenAt = DateTime.SpecifyKind(row.LastSeenAt, DateTimeKind.Utc),
            ExpiresAt = DateTime.SpecifyKind(row.ExpiresAt, DateTimeKind.Utc),
        }).ToList();

        return CursorPageBuilder.Build(
            items,
            keyset,
            cursor,
            sort,
            filterHash,
            item => [.. sort.Terms.Select(term => SessionSortFields.ValueOf(item, term.Field))],
            totalCount);
    }
}
