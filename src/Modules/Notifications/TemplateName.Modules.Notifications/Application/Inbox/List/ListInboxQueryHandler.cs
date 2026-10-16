using System.Data;
using System.Globalization;
using Dapper;
using TemplateName.Application.Common.Data;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Application.Inbox.List;

/// <summary>
/// Pages the caller's in-app notifications by keyset on <c>(CreatedAt, Id)</c> (ADR 0010). The SQL is assembled only from constants and
/// <see cref="KeysetQuery"/> clauses, whose column names come from <see cref="InAppNotificationSortFields"/>; every value is a
/// parameter. Dapper bypasses the EF Core model, so the query itself keeps to the caller's rows (<c>UserId = @UserId</c>) and, for
/// <see cref="ListInboxQuery.UnreadOnly"/>, to the unread ones (<c>ReadAt IS NULL</c>). The notify tables have no soft delete, so there
/// is no <c>IsDeleted</c> filter. The cursor is bound to the user and to <c>unreadOnly</c> through the filter hash, so a cursor made for
/// one list cannot be replayed against the other. A caller without a user id gets an empty page.
/// </summary>
internal sealed class ListInboxQueryHandler(IDbConnectionFactory connectionFactory, ICurrentUser currentUser)
    : IQueryHandler<ListInboxQuery, CursorPage<InAppNotificationResponse>>
{
    private const string OwnerFilter = "UserId = @UserId";
    private const string UnreadFilter = " AND ReadAt IS NULL";

    public async Task<Result<CursorPage<InAppNotificationResponse>>> HandleAsync(ListInboxQuery query, CancellationToken cancellationToken)
    {
        var sortResult = SortSpecification.Parse(
            query.Page.Sort,
            InAppNotificationSortFields.Allowed,
            InAppNotificationSortFields.Id,
            InAppNotificationSortFields.Default);
        if (sortResult.IsFailure)
        {
            return sortResult.Error;
        }

        var sort = sortResult.Value;
        var userId = currentUser.UserId ?? Guid.Empty;
        var filterHash = CursorCodec.ComputeFilterHash(
            string.Create(CultureInfo.InvariantCulture, $"userId={userId};unreadOnly={query.UnreadOnly}"));

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

        var filter = query.UnreadOnly ? OwnerFilter + UnreadFilter : OwnerFilter;

        // The columns follow the InAppNotificationResponse constructor, which Dapper binds by position.
        var pageSql = $"""
            SELECT TOP (@Take) Id, TypeCode, Category, Title, Body, CreatedAt, ReadAt
            FROM [notify].[InAppNotifications]
            WHERE {filter} AND {keyset.WhereClause}
            ORDER BY {keyset.OrderByClause}
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<InAppNotificationResponse>(new CommandDefinition(pageSql, parameters, cancellationToken: cancellationToken));

        long? totalCount = null;
        if (query.Page.IncludeTotalCount)
        {
            var countSql = $"SELECT COUNT_BIG(*) FROM [notify].[InAppNotifications] WHERE {filter}";
            totalCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition(countSql, parameters, cancellationToken: cancellationToken));
        }

        // datetime2 reads back as DateTimeKind.Unspecified; the columns hold UTC. The cursor keys are taken from these rows, so they
        // carry exactly the millisecond values SQL Server stored.
        var items = rows.Select(row => row with
        {
            CreatedAt = DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc),
            ReadAt = row.ReadAt is { } readAt ? DateTime.SpecifyKind(readAt, DateTimeKind.Utc) : null,
        }).ToList();

        return CursorPageBuilder.Build(
            items,
            keyset,
            cursor,
            sort,
            filterHash,
            item => [.. sort.Terms.Select(term => InAppNotificationSortFields.ValueOf(item, term.Field))],
            totalCount);
    }
}
