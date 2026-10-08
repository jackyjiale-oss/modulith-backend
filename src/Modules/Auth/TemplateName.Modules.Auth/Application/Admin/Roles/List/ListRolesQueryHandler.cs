using Dapper;
using TemplateName.Application.Common.Data;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Admin.Roles.List;

/// <summary>
/// Pages the roles that are not deleted by keyset (ADR 0010). The SQL is assembled only from constants and <see cref="KeysetQuery"/>
/// clauses, whose column names come from <see cref="RoleSortFields"/>; every value is a parameter. Dapper bypasses the EF Core
/// soft-delete filter, so the query repeats it (<c>IsDeleted = 0</c>, ADR 0006). The list carries the number of grants, not the grants
/// themselves (<c>GET .../roles/{id}</c> has those); nothing in it is personal data.
/// </summary>
internal sealed class ListRolesQueryHandler(IDbConnectionFactory connectionFactory) : IQueryHandler<ListRolesQuery, CursorPage<RoleListItemResponse>>
{
    private const string Filter = "IsDeleted = 0";

    public async Task<Result<CursorPage<RoleListItemResponse>>> HandleAsync(ListRolesQuery query, CancellationToken cancellationToken)
    {
        var sortResult = SortSpecification.Parse(query.Page.Sort, RoleSortFields.Allowed, RoleSortFields.Id, RoleSortFields.Default);
        if (sortResult.IsFailure)
        {
            return sortResult.Error;
        }

        var sort = sortResult.Value;
        var filterHash = CursorCodec.ComputeFilterHash(string.Empty);

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

        // The columns follow the RoleListItemResponse constructor, which Dapper binds by position.
        var pageSql = $"""
            SELECT TOP (@Take) Id, Name, Description, IsSystem,
                (SELECT COUNT(*) FROM [auth].[RolePermissions] AS grants WHERE grants.RoleId = [auth].[Roles].Id) AS PermissionCount,
                CreatedAt
            FROM [auth].[Roles]
            WHERE {Filter} AND {keyset.WhereClause}
            ORDER BY {keyset.OrderByClause}
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<RoleListItemResponse>(new CommandDefinition(pageSql, parameters, cancellationToken: cancellationToken));

        long? totalCount = null;
        if (query.Page.IncludeTotalCount)
        {
            var countSql = $"SELECT COUNT_BIG(*) FROM [auth].[Roles] WHERE {Filter}";
            totalCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition(countSql, parameters, cancellationToken: cancellationToken));
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
            item => [.. sort.Terms.Select(term => RoleSortFields.ValueOf(item, term.Field))],
            totalCount);
    }
}
