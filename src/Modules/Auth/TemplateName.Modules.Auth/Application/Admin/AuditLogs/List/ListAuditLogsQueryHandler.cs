using System.Data;
using System.Globalization;
using System.Text;
using Dapper;
using TemplateName.Application.Common.Data;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Admin.AuditLogs.List;

/// <summary>
/// Pages the audit log by keyset (ADR 0010), newest first by default, with the entry's <c>Id</c> as the unique tie-breaker so entries
/// that share a timestamp are neither skipped nor repeated. The SQL is assembled only from constants and <see cref="KeysetQuery"/>
/// clauses, whose column names come from <see cref="AuditLogSortFields"/>; every filter value is a parameter. The log has no soft
/// delete. The columns selected hold no secret (the log never receives one; the attempted identifier was masked on write). A cursor is
/// tied to the filters it was issued for.
/// </summary>
internal sealed class ListAuditLogsQueryHandler(IDbConnectionFactory connectionFactory)
    : IQueryHandler<ListAuditLogsQuery, CursorPage<AuditLogResponse>>
{
    public async Task<Result<CursorPage<AuditLogResponse>>> HandleAsync(ListAuditLogsQuery query, CancellationToken cancellationToken)
    {
        var sortResult = SortSpecification.Parse(query.Page.Sort, AuditLogSortFields.Allowed, AuditLogSortFields.Id, AuditLogSortFields.Default);
        if (sortResult.IsFailure)
        {
            return sortResult.Error;
        }

        var sort = sortResult.Value;
        var parameters = new DynamicParameters();
        var filter = new StringBuilder("1 = 1");

        // The validator has already checked these; a blank value is no filter.
        var eventType = string.IsNullOrWhiteSpace(query.EventType) ? null : query.EventType;
        DateTime? from = AuditLogTimestamp.TryParse(query.From, out var fromUtc) ? fromUtc : null;
        DateTime? to = AuditLogTimestamp.TryParse(query.To, out var toUtc) ? toUtc : null;

        if (query.UserId is { } userId)
        {
            filter.Append(" AND UserId = @UserId");
            parameters.Add("UserId", userId, DbType.Guid);
        }

        if (eventType is not null)
        {
            filter.Append(" AND EventType = @EventType");
            parameters.Add("EventType", eventType, DbType.String);
        }

        if (query.Succeeded is { } succeeded)
        {
            filter.Append(" AND Succeeded = @Succeeded");
            parameters.Add("Succeeded", succeeded, DbType.Boolean);
        }

        // datetime2 like the column (Dapper's default DateTime is datetime, which rounds).
        if (from is { } start)
        {
            filter.Append(" AND OccurredAt >= @From");
            parameters.Add("From", start, DbType.DateTime2);
        }

        if (to is { } end)
        {
            filter.Append(" AND OccurredAt <= @To");
            parameters.Add("To", end, DbType.DateTime2);
        }

        var filterHash = CursorCodec.ComputeFilterHash(string.Create(
            CultureInfo.InvariantCulture,
            $"userId={query.UserId};eventType={eventType};succeeded={query.Succeeded};from={from:O};to={to:O}"));

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

        // The columns follow the AuditLogResponse constructor, which Dapper binds by position.
        var pageSql = $"""
            SELECT TOP (@Take) Id, OccurredAt, UserId, EventType, Succeeded, FailureReason, AttemptedIdentifier, IpAddress, UserAgent,
                SessionId, TraceId, Details
            FROM [auth].[AuthAuditLogs]
            WHERE {filter} AND {keyset.WhereClause}
            ORDER BY {keyset.OrderByClause}
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<AuditLogResponse>(new CommandDefinition(pageSql, parameters, cancellationToken: cancellationToken));

        long? totalCount = null;
        if (query.Page.IncludeTotalCount)
        {
            var countSql = $"SELECT COUNT_BIG(*) FROM [auth].[AuthAuditLogs] WHERE {filter}";
            totalCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition(countSql, parameters, cancellationToken: cancellationToken));
        }

        // datetime2 reads back as DateTimeKind.Unspecified; the column holds UTC. The cursor keys are taken from these rows, so they
        // carry exactly the millisecond values SQL Server stored.
        var items = rows.Select(row => row with { OccurredAt = DateTime.SpecifyKind(row.OccurredAt, DateTimeKind.Utc) }).ToList();

        return CursorPageBuilder.Build(
            items,
            keyset,
            cursor,
            sort,
            filterHash,
            item => [.. sort.Terms.Select(term => AuditLogSortFields.ValueOf(item, term.Field))],
            totalCount);
    }
}
