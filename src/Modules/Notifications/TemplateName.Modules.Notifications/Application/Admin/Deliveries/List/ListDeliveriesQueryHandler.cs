using System.Data;
using System.Globalization;
using System.Text;
using Dapper;
using TemplateName.Application.Common.Data;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Application.Admin.Deliveries.List;

/// <summary>
/// Pages the deliveries by keyset on <c>(CreatedAt, Id)</c> (ADR 0010), newest first by default. The SQL is assembled only from
/// constants and <see cref="KeysetQuery"/> clauses, whose column names come from <see cref="DeliverySortFields"/>; every filter value
/// is a parameter. A delivery has no type or recipient of its own, so the query joins its notification; the notify tables have no soft
/// delete, so there is no <c>IsDeleted</c> filter to forget. The cursor is bound to all four filters through the filter hash, so a
/// cursor made for one list cannot be replayed against another. The destination is masked here (<see cref="DestinationMask"/>), before
/// the rows leave the handler, so no code path can return the real address; the other columns are not secret (<c>LastError</c> is a
/// reason code).
/// </summary>
internal sealed class ListDeliveriesQueryHandler(IDbConnectionFactory connectionFactory)
    : IQueryHandler<ListDeliveriesQuery, CursorPage<DeliveryResponse>>
{
    private const string FromClause = "[notify].[Deliveries] AS d INNER JOIN [notify].[Notifications] AS n ON n.Id = d.NotificationId";

    public async Task<Result<CursorPage<DeliveryResponse>>> HandleAsync(ListDeliveriesQuery query, CancellationToken cancellationToken)
    {
        var sortResult = SortSpecification.Parse(query.Page.Sort, DeliverySortFields.Allowed, DeliverySortFields.Id, DeliverySortFields.Default);
        if (sortResult.IsFailure)
        {
            return sortResult.Error;
        }

        var sort = sortResult.Value;
        var parameters = new DynamicParameters();
        var filter = new StringBuilder("1 = 1");

        // The validator has already checked these; a blank type code is no filter.
        var typeCode = string.IsNullOrWhiteSpace(query.TypeCode) ? null : query.TypeCode;

        if (query.Status is { } status)
        {
            filter.Append(" AND d.Status = @Status");
            parameters.Add("Status", (byte)status, DbType.Byte);
        }

        if (query.Channel is { } channel)
        {
            filter.Append(" AND d.Channel = @Channel");
            parameters.Add("Channel", (byte)channel, DbType.Byte);
        }

        if (typeCode is not null)
        {
            filter.Append(" AND n.TypeCode = @TypeCode");
            parameters.Add("TypeCode", typeCode, DbType.String);
        }

        if (query.RecipientUserId is { } recipientUserId)
        {
            filter.Append(" AND n.RecipientUserId = @RecipientUserId");
            parameters.Add("RecipientUserId", recipientUserId, DbType.Guid);
        }

        var filterHash = CursorCodec.ComputeFilterHash(string.Create(
            CultureInfo.InvariantCulture,
            $"status={query.Status};channel={query.Channel};typeCode={typeCode};recipientUserId={query.RecipientUserId}"));

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
        parameters.AddDynamicParams(keyset.Parameters);
        parameters.Add("Take", keyset.Take);

        // The columns follow the DeliveryResponse constructor, which Dapper binds by position.
        var pageSql = $"""
            SELECT TOP (@Take) d.Id, d.NotificationId, n.TypeCode, n.RecipientUserId, d.Channel, d.Destination, d.Status, d.AttemptCount,
                d.NextAttemptAt, d.LastError, d.CreatedAt, d.SentAt, d.DeadLetteredAt
            FROM {FromClause}
            WHERE {filter} AND {keyset.WhereClause}
            ORDER BY {keyset.OrderByClause}
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<DeliveryResponse>(new CommandDefinition(pageSql, parameters, cancellationToken: cancellationToken));

        long? totalCount = null;
        if (query.Page.IncludeTotalCount)
        {
            var countSql = $"SELECT COUNT_BIG(*) FROM {FromClause} WHERE {filter}";
            totalCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition(countSql, parameters, cancellationToken: cancellationToken));
        }

        // datetime2 reads back as DateTimeKind.Unspecified; the columns hold UTC. The cursor keys are taken from these rows, so they
        // carry exactly the millisecond values SQL Server stored.
        var items = rows.Select(row => row with
        {
            Destination = DestinationMask.Mask(row.Destination),
            NextAttemptAt = AsUtc(row.NextAttemptAt),
            CreatedAt = DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc),
            SentAt = AsUtc(row.SentAt),
            DeadLetteredAt = AsUtc(row.DeadLetteredAt),
        }).ToList();

        return CursorPageBuilder.Build(
            items,
            keyset,
            cursor,
            sort,
            filterHash,
            item => [.. sort.Terms.Select(term => DeliverySortFields.ValueOf(item, term.Field))],
            totalCount);
    }

    private static DateTime? AsUtc(DateTime? value) => value is { } instant ? DateTime.SpecifyKind(instant, DateTimeKind.Utc) : null;
}
