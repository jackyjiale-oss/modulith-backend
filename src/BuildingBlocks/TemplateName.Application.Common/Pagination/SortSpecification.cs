using TemplateName.SharedKernel;

namespace TemplateName.Application.Common.Pagination;

/// <summary>
/// A parsed, allow-listed sort that always ends with the unique id, so the order is total and keyset paging never skips or repeats a
/// row. <see cref="Signature"/> is its canonical text (<c>-createdAt,-id</c>), which cursors carry to detect a changed sort.
/// </summary>
public sealed class SortSpecification
{
    private SortSpecification(IReadOnlyList<(SortField Field, bool Descending)> terms)
    {
        Terms = terms;
        Signature = string.Join(',', terms.Select(term => (term.Descending ? "-" : string.Empty) + term.Field.Name));
    }

    /// <summary>The sort terms in order; the last one is the id tie-breaker.</summary>
    public IReadOnlyList<(SortField Field, bool Descending)> Terms { get; }

    public string Signature { get; }

    /// <summary>
    /// Parses <paramref name="sort"/>: comma-separated field names, each optionally prefixed with <c>-</c> for descending. Names are
    /// matched exactly against <paramref name="allowedFields"/>; an empty sort means <paramref name="defaultSort"/>.
    /// <paramref name="idField"/> is appended with the direction of the last term. An unknown, empty or repeated field (including the
    /// id itself) fails with <see cref="PaginationErrors.InvalidSort"/>.
    /// </summary>
    public static Result<SortSpecification> Parse(string? sort, IReadOnlyList<SortField> allowedFields, SortField idField, string defaultSort)
    {
        var text = string.IsNullOrWhiteSpace(sort) ? defaultSort : sort;
        var terms = new List<(SortField Field, bool Descending)>();

        foreach (var rawTerm in text.Split(','))
        {
            var term = rawTerm.Trim();
            var isDescending = term.StartsWith('-');
            var name = isDescending ? term[1..] : term;
            var field = allowedFields.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));

            if (field is null || terms.Exists(existing => existing.Field == field))
            {
                return PaginationErrors.InvalidSort(allowedFields.Select(allowed => allowed.Name));
            }

            terms.Add((field, isDescending));
        }

        terms.Add((idField, terms[^1].Descending));

        return new SortSpecification(terms);
    }
}
