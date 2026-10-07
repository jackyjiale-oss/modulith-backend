using System.Data;
using System.Globalization;
using System.Text;
using Dapper;

namespace TemplateName.Application.Common.Pagination;

/// <summary>
/// Builds the keyset predicate and order of a list query (review P4, P8). Column text comes only from <see cref="SortField.Column"/>,
/// which is defined in code; every cursor value is a parameter.
/// </summary>
public static class KeysetSqlBuilder
{
    private const string NoCursorPredicate = "1 = 1";

    /// <summary>
    /// Without a cursor the predicate is <c>1 = 1</c>. With one it is the expanded row comparison
    /// <c>((c0 ⋚ @k0) OR (c0 = @k0 AND c1 ⋚ @k1) …)</c>, where <c>⋚</c> is <c>&lt;</c> for a descending term and <c>&gt;</c> for an
    /// ascending one. A <see cref="CursorDirection.Previous"/> cursor flips both the comparisons and the order; the caller re-reverses
    /// the rows (<see cref="CursorPageBuilder"/>). <see cref="KeysetQuery.Take"/> is <paramref name="pageSize"/> + 1, so the extra row
    /// tells whether another page exists.
    /// </summary>
    /// <exception cref="ArgumentException">The cursor does not have one key value per sort term.</exception>
    public static KeysetQuery Build(SortSpecification sort, Cursor? cursor, int pageSize)
    {
        var isBackward = cursor?.Direction == CursorDirection.Previous;
        var parameters = new DynamicParameters();
        var orderBy = string.Join(", ", sort.Terms.Select(term => $"{term.Field.Column} {(term.Descending != isBackward ? "DESC" : "ASC")}"));

        if (cursor is null)
        {
            return new KeysetQuery(NoCursorPredicate, orderBy, parameters, pageSize + 1, IsBackward: false);
        }

        if (cursor.KeyValues.Count != sort.Terms.Count)
        {
            throw new ArgumentException("A cursor must have one key value per sort term.", nameof(cursor));
        }

        for (var i = 0; i < cursor.KeyValues.Count; i++)
        {
            AddKeyParameter(parameters, ParameterName(i), cursor.KeyValues[i]);
        }

        var where = new StringBuilder("(");
        for (var i = 0; i < sort.Terms.Count; i++)
        {
            if (i > 0)
            {
                where.Append(" OR ");
            }

            where.Append('(');
            for (var j = 0; j < i; j++)
            {
                where.Append(CultureInfo.InvariantCulture, $"{sort.Terms[j].Field.Column} = @{ParameterName(j)} AND ");
            }

            var comparison = sort.Terms[i].Descending != isBackward ? "<" : ">";
            where.Append(CultureInfo.InvariantCulture, $"{sort.Terms[i].Field.Column} {comparison} @{ParameterName(i)})");
        }

        where.Append(')');

        return new KeysetQuery(where.ToString(), orderBy, parameters, pageSize + 1, isBackward);
    }

    private static string ParameterName(int index) => string.Create(CultureInfo.InvariantCulture, $"k{index}");

    // Dapper sends DateTime as SQL datetime, which rounds to 1/300 s and would compare unequal to the datetime2(3) value the cursor
    // was read from; datetime2 keeps it exact.
    private static void AddKeyParameter(DynamicParameters parameters, string name, object value)
    {
        if (value is DateTime)
        {
            parameters.Add(name, value, DbType.DateTime2);
        }
        else
        {
            parameters.Add(name, value);
        }
    }
}
