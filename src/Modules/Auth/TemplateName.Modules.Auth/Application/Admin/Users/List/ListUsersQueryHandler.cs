using System.Data;
using System.Globalization;
using System.Text;
using Dapper;
using TemplateName.Application.Common.Data;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Admin.Users.List;

/// <summary>
/// Pages users by keyset (ADR 0010). The SQL is assembled only from constants and <see cref="KeysetQuery"/> clauses, whose column names
/// come from <see cref="UserSortFields"/>; every value is a parameter. Dapper bypasses the EF Core soft-delete filter, so the query
/// repeats it (<c>IsDeleted = 0</c>, ADR 0006). The search is a literal prefix of the normalized email: the caller's text is trimmed,
/// upper-cased like <see cref="User.NormalizeEmail"/> and its <c>LIKE</c> wildcards escaped, so <c>a_b</c> finds only addresses that
/// start with <c>A_B</c>. The selected columns leave out every secret (hash, stamp, history).
/// </summary>
internal sealed class ListUsersQueryHandler(IDbConnectionFactory connectionFactory, TimeProvider timeProvider)
    : IQueryHandler<ListUsersQuery, CursorPage<UserListItemResponse>>
{
    private const char LikeEscape = '\\';

    // The prefix filter. ESCAPE names LikeEscape; NormalizedEmail's filtered unique index serves it.
    private const string SearchFilter = " AND NormalizedEmail LIKE @SearchPrefix ESCAPE '\\'";

    public async Task<Result<CursorPage<UserListItemResponse>>> HandleAsync(ListUsersQuery query, CancellationToken cancellationToken)
    {
        var sortResult = SortSpecification.Parse(query.Page.Sort, UserSortFields.Allowed, UserSortFields.Id, UserSortFields.Default);
        if (sortResult.IsFailure)
        {
            return sortResult.Error;
        }

        var sort = sortResult.Value;
        var prefix = string.IsNullOrWhiteSpace(query.Search) ? null : ToPrefixPattern(query.Search);
        var filterHash = CursorCodec.ComputeFilterHash(
            string.Create(CultureInfo.InvariantCulture, $"search={prefix};status={(int?)query.Status}"));

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
        // datetime2 like the column (Dapper's default DateTime is datetime, which rounds).
        parameters.Add("Now", timeProvider.GetUtcNow().UtcDateTime, DbType.DateTime2);

        // Dapper bypasses the EF Core soft-delete filter, so the query repeats it (ADR 0006).
        var filter = "IsDeleted = 0";
        if (prefix is not null)
        {
            filter += SearchFilter;
            parameters.Add("SearchPrefix", prefix);
        }

        if (query.Status is { } status)
        {
            filter += " AND Status = @Status";
            parameters.Add("Status", (int)status);
        }

        // The columns follow the UserListItemResponse constructor, which Dapper binds by position.
        var pageSql = $"""
            SELECT TOP (@Take) Id, Email, DisplayName, Status, EmailConfirmed,
                CAST(CASE WHEN LockoutEnd > @Now THEN 1 ELSE 0 END AS bit) AS IsLockedOut, CreatedAt, LastLoginAt
            FROM [auth].[Users]
            WHERE {filter} AND {keyset.WhereClause}
            ORDER BY {keyset.OrderByClause}
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<UserListItemResponse>(new CommandDefinition(pageSql, parameters, cancellationToken: cancellationToken));

        long? totalCount = null;
        if (query.Page.IncludeTotalCount)
        {
            var countSql = $"SELECT COUNT_BIG(*) FROM [auth].[Users] WHERE {filter}";
            totalCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition(countSql, parameters, cancellationToken: cancellationToken));
        }

        // datetime2 reads back as DateTimeKind.Unspecified; the columns hold UTC. The cursor keys are taken from these rows, so they
        // carry exactly the millisecond values SQL Server stored.
        var items = rows.Select(row => row with
        {
            CreatedAt = DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc),
            LastLoginAt = row.LastLoginAt is { } lastLoginAt ? DateTime.SpecifyKind(lastLoginAt, DateTimeKind.Utc) : null,
        }).ToList();

        return CursorPageBuilder.Build(
            items,
            keyset,
            cursor,
            sort,
            filterHash,
            item => [.. sort.Terms.Select(term => UserSortFields.ValueOf(item, term.Field))],
            totalCount);
    }

    /// <summary>
    /// The <c>LIKE</c> pattern for addresses starting with <paramref name="search"/>: normalized like <see cref="User.NormalizeEmail"/>,
    /// with <c>%</c>, <c>_</c>, <c>[</c> and the escape character itself escaped, then <c>%</c> appended.
    /// </summary>
    internal static string ToPrefixPattern(string search)
    {
        var normalized = User.NormalizeEmail(search);
        var pattern = new StringBuilder(normalized.Length + 1);
        foreach (var character in normalized)
        {
            if (character is '%' or '_' or '[' or LikeEscape)
            {
                pattern.Append(LikeEscape);
            }

            pattern.Append(character);
        }

        return pattern.Append('%').ToString();
    }
}
