using System.Data;
using Dapper;
using TemplateName.Application.Common.Data;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Application.Inbox.UnreadCount;

/// <summary>
/// Counts the caller's unread notifications through Dapper, looking at no more than <see cref="Cap"/> rows: a user with a very large
/// backlog costs the same as one with a thousand, and the badge only needs "1000+" (the filtered index
/// <c>IX_InAppNotifications_UserId</c> on unread rows serves it). The owner filter is in the SQL. A caller without a user id counts nothing.
/// </summary>
internal sealed class GetUnreadCountQueryHandler(IDbConnectionFactory connectionFactory, ICurrentUser currentUser)
    : IQueryHandler<GetUnreadCountQuery, UnreadCountResponse>
{
    /// <summary>The most unread notifications the query looks at.</summary>
    public const int Cap = 1000;

    private const string Sql = """
        SELECT COUNT(*)
        FROM (
            SELECT TOP (@Cap) 1 AS x
            FROM [notify].[InAppNotifications]
            WHERE UserId = @UserId AND ReadAt IS NULL) AS t
        """;

    public async Task<Result<UnreadCountResponse>> HandleAsync(GetUnreadCountQuery query, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? Guid.Empty;
        var parameters = new DynamicParameters();
        parameters.Add("UserId", userId, DbType.Guid);
        parameters.Add("Cap", Cap);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(Sql, parameters, cancellationToken: cancellationToken));

        return new UnreadCountResponse(count, IsCapped: count >= Cap);
    }
}
