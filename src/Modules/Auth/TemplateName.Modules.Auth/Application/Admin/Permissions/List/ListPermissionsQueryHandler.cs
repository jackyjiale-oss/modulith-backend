using System.Globalization;
using Dapper;
using TemplateName.Application.Common.Data;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Admin.Permissions.List;

/// <summary>
/// Pages the declared permissions by keyset (ADR 0010), sorted by code. Permissions that no module declares any more
/// (<c>IsDeprecated = 1</c>) are left out unless the caller asks for them. The SQL is assembled only from constants and
/// <see cref="KeysetQuery"/> clauses, whose column names come from <see cref="PermissionSortFields"/>. The table has no soft-delete
/// column (permissions are deprecated, never deleted). A cursor is tied to <c>includeDeprecated</c>. Nothing in it is personal data.
/// </summary>
internal sealed class ListPermissionsQueryHandler(IDbConnectionFactory connectionFactory)
    : IQueryHandler<ListPermissionsQuery, CursorPage<PermissionResponse>>
{
    public async Task<Result<CursorPage<PermissionResponse>>> HandleAsync(ListPermissionsQuery query, CancellationToken cancellationToken)
    {
        var sortResult = SortSpecification.Parse(query.Page.Sort, PermissionSortFields.Allowed, PermissionSortFields.Id, PermissionSortFields.Default);
        if (sortResult.IsFailure)
        {
            return sortResult.Error;
        }

        var sort = sortResult.Value;
        var filterHash = CursorCodec.ComputeFilterHash(string.Create(CultureInfo.InvariantCulture, $"includeDeprecated={query.IncludeDeprecated}"));

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

        var filter = query.IncludeDeprecated ? "1 = 1" : "IsDeprecated = 0";

        // The columns follow the PermissionResponse constructor, which Dapper binds by position.
        var pageSql = $"""
            SELECT TOP (@Take) Id, Code, Module, Name, Description, IsDeprecated
            FROM [auth].[Permissions]
            WHERE {filter} AND {keyset.WhereClause}
            ORDER BY {keyset.OrderByClause}
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var items = (await connection.QueryAsync<PermissionResponse>(new CommandDefinition(pageSql, parameters, cancellationToken: cancellationToken))).ToList();

        long? totalCount = null;
        if (query.Page.IncludeTotalCount)
        {
            var countSql = $"SELECT COUNT_BIG(*) FROM [auth].[Permissions] WHERE {filter}";
            totalCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition(countSql, parameters, cancellationToken: cancellationToken));
        }

        return CursorPageBuilder.Build(
            items,
            keyset,
            cursor,
            sort,
            filterHash,
            item => [.. sort.Terms.Select(term => PermissionSortFields.ValueOf(item, term.Field))],
            totalCount);
    }
}
