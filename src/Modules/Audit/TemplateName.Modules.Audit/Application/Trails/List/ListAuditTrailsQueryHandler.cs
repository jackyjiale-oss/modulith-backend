using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using TemplateName.Application.Common.Data;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Audit.Application.Trails.List;

/// <summary>
/// Pages the audit trail by keyset (ADR 0010), newest first by default, with the line's <c>Id</c> as the unique tie-breaker so lines
/// that share a timestamp are neither skipped nor repeated. The SQL is assembled only from constants and <see cref="KeysetQuery"/>
/// clauses, whose column names come from <see cref="AuditTrailSortFields"/>; every filter value is a parameter. The trail has no soft
/// delete. <c>Changes</c> is stored as JSON text (the table refuses anything that is not JSON) and is returned as a JSON object. A cursor
/// is tied to the filters it was issued for: all nine go into the filter hash.
/// </summary>
internal sealed class ListAuditTrailsQueryHandler(IDbConnectionFactory connectionFactory)
    : IQueryHandler<ListAuditTrailsQuery, CursorPage<AuditTrailResponse>>
{
    public async Task<Result<CursorPage<AuditTrailResponse>>> HandleAsync(ListAuditTrailsQuery query, CancellationToken cancellationToken)
    {
        var sortResult = SortSpecification.Parse(query.Page.Sort, AuditTrailSortFields.Allowed, AuditTrailSortFields.Id, AuditTrailSortFields.Default);
        if (sortResult.IsFailure)
        {
            return sortResult.Error;
        }

        var sort = sortResult.Value;
        var parameters = new DynamicParameters();
        var filter = new StringBuilder("1 = 1");

        // The validator has already checked these; a blank value is no filter.
        var entityType = string.IsNullOrWhiteSpace(query.EntityType) ? null : query.EntityType;
        var entityId = string.IsNullOrWhiteSpace(query.EntityId) ? null : query.EntityId;
        var module = string.IsNullOrWhiteSpace(query.Module) ? null : query.Module;
        var action = string.IsNullOrWhiteSpace(query.Action) ? null : query.Action;
        DateTime? from = AuditTrailTimestamp.TryParse(query.From, out var fromUtc) ? fromUtc : null;
        DateTime? to = AuditTrailTimestamp.TryParse(query.To, out var toUtc) ? toUtc : null;

        if (entityType is not null)
        {
            filter.Append(" AND EntityType = @EntityType");
            parameters.Add("EntityType", entityType, DbType.String);
        }

        if (entityId is not null)
        {
            filter.Append(" AND EntityId = @EntityId");
            parameters.Add("EntityId", entityId, DbType.String);
        }

        if (query.UserId is { } userId)
        {
            filter.Append(" AND UserId = @UserId");
            parameters.Add("UserId", userId, DbType.Guid);
        }

        if (query.ChangeSetId is { } changeSetId)
        {
            filter.Append(" AND ChangeSetId = @ChangeSetId");
            parameters.Add("ChangeSetId", changeSetId, DbType.Guid);
        }

        if (module is not null)
        {
            filter.Append(" AND Module = @Module");
            parameters.Add("Module", module, DbType.String);
        }

        // The column is varchar: an ANSI parameter keeps the comparison from converting the column.
        if (action is not null)
        {
            filter.Append(" AND Action = @Action");
            parameters.Add("Action", action, DbType.AnsiString);
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
            $"entityType={entityType};entityId={entityId};userId={query.UserId};changeSetId={query.ChangeSetId};module={module};action={action};from={from:O};to={to:O}"));

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

        // The columns follow the AuditTrailRow constructor, which Dapper binds by position.
        var pageSql = $"""
            SELECT TOP (@Take) Id, ChangeSetId, OccurredAt, UserId, SessionId, Module, EntityType, EntityId, Action, Changes, TraceId
            FROM [audit].[AuditTrails]
            WHERE {filter} AND {keyset.WhereClause}
            ORDER BY {keyset.OrderByClause}
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<AuditTrailRow>(new CommandDefinition(pageSql, parameters, cancellationToken: cancellationToken));

        long? totalCount = null;
        if (query.Page.IncludeTotalCount)
        {
            var countSql = $"SELECT COUNT_BIG(*) FROM [audit].[AuditTrails] WHERE {filter}";
            totalCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition(countSql, parameters, cancellationToken: cancellationToken));
        }

        // datetime2 reads back as DateTimeKind.Unspecified; the column holds UTC. The cursor keys are taken from these rows, so they
        // carry exactly the millisecond values SQL Server stored.
        var items = rows.Select(ToResponse).ToList();

        return CursorPageBuilder.Build(
            items,
            keyset,
            cursor,
            sort,
            filterHash,
            item => [.. sort.Terms.Select(term => AuditTrailSortFields.ValueOf(item, term.Field))],
            totalCount);
    }

    private static AuditTrailResponse ToResponse(AuditTrailRow row)
    {
        // The column has a JSON check, so the text parses; the document is cloned so the element outlives it.
        using var document = JsonDocument.Parse(row.Changes);

        return new AuditTrailResponse(
            row.Id,
            row.ChangeSetId,
            DateTime.SpecifyKind(row.OccurredAt, DateTimeKind.Utc),
            row.UserId,
            row.SessionId,
            row.Module,
            row.EntityType,
            row.EntityId,
            row.Action,
            document.RootElement.Clone(),
            row.TraceId);
    }

    // What the query selects, with Changes still text: Dapper cannot read a column into a JsonElement.
    private sealed record AuditTrailRow(
        long Id,
        Guid ChangeSetId,
        DateTime OccurredAt,
        Guid? UserId,
        Guid? SessionId,
        string Module,
        string EntityType,
        string EntityId,
        string Action,
        string Changes,
        string? TraceId);
}
