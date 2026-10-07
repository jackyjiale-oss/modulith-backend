using Dapper;

namespace TemplateName.Application.Common.Pagination;

/// <summary>
/// The keyset parts of a list query, built by <see cref="KeysetSqlBuilder"/>: a predicate and an <c>ORDER BY</c> list to put into the
/// SQL, the key-value parameters (<c>@k0</c>, <c>@k1</c>, …) they use, and the number of rows to fetch (page size + 1).
/// </summary>
public sealed record KeysetQuery(string WhereClause, string OrderByClause, DynamicParameters Parameters, int Take, bool IsBackward);
