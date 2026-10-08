using System.Globalization;
using Dapper;
using TemplateName.Application.Common.Data;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Sample.Application.LeaveRequests.List;

/// <summary>
/// Pages leave requests by keyset (ADR 0010). The SQL is assembled only from constants and <see cref="KeysetQuery"/> clauses, whose
/// column names come from <see cref="LeaveRequestSortFields"/>; every value is a parameter.
/// </summary>
internal sealed class ListLeaveRequestsQueryHandler(IDbConnectionFactory connectionFactory)
    : IQueryHandler<ListLeaveRequestsQuery, CursorPage<LeaveRequestListItemResponse>>
{
    public async Task<Result<CursorPage<LeaveRequestListItemResponse>>> HandleAsync(
        ListLeaveRequestsQuery query,
        CancellationToken cancellationToken)
    {
        var sortResult = SortSpecification.Parse(
            query.Page.Sort, LeaveRequestSortFields.Allowed, LeaveRequestSortFields.Id, LeaveRequestSortFields.Default);
        if (sortResult.IsFailure)
        {
            return sortResult.Error;
        }

        var sort = sortResult.Value;
        var filterHash = CursorCodec.ComputeFilterHash(
            string.Create(CultureInfo.InvariantCulture, $"employeeId={query.EmployeeId};status={query.Status}"));

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

        // Dapper bypasses the EF Core soft-delete filter, so the query repeats it (ADR 0006).
        var filter = "IsDeleted = 0";
        if (query.EmployeeId is { } employeeId)
        {
            filter += " AND EmployeeId = @EmployeeId";
            parameters.Add("EmployeeId", employeeId);
        }

        if (query.Status is { } status)
        {
            filter += " AND Status = @Status";
            parameters.Add("Status", (byte)status);
        }

        // The columns follow the LeaveRequestListItemResponse constructor, which Dapper binds by position.
        var pageSql = $"""
            SELECT TOP (@Take) Id, EmployeeId, StartDate, EndDate, Status, CreatedAt
            FROM [sample].[LeaveRequests]
            WHERE {filter} AND {keyset.WhereClause}
            ORDER BY {keyset.OrderByClause}
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<LeaveRequestListItemResponse>(
            new CommandDefinition(pageSql, parameters, cancellationToken: cancellationToken));

        long? totalCount = null;
        if (query.Page.IncludeTotalCount)
        {
            var countSql = $"SELECT COUNT_BIG(*) FROM [sample].[LeaveRequests] WHERE {filter}";
            totalCount = await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(countSql, parameters, cancellationToken: cancellationToken));
        }

        // datetime2 reads back as DateTimeKind.Unspecified; the column holds UTC. The cursor keys are taken from these rows, so they
        // carry exactly the millisecond values SQL Server stored.
        var items = rows.Select(row => row with { CreatedAt = DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc) }).ToList();

        return CursorPageBuilder.Build(
            items,
            keyset,
            cursor,
            sort,
            filterHash,
            item => [.. sort.Terms.Select(term => LeaveRequestSortFields.ValueOf(item, term.Field))],
            totalCount);
    }
}
