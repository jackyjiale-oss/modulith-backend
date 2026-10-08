using TemplateName.SharedKernel;

namespace TemplateName.Application.Common.Pagination;

/// <summary>The errors of cursor pagination. Their messages are in <c>CommonErrorMessages</c> (Web.Common) and its translations.</summary>
public static class PaginationErrors
{
    public static readonly Error InvalidCursor = Error.Validation(
        "pagination.invalid_cursor",
        "The cursor is not valid. Start again from the first page.");

    public static readonly Error CursorMismatch = Error.Validation(
        "pagination.cursor_mismatch",
        "The cursor was issued for a different sort or filter. Start again from the first page.");

    public static Error InvalidSort(IEnumerable<string> allowed)
    {
        // Tolerates null so the architecture tests can collect this error with default arguments.
        var allowedNames = string.Join(',', allowed ?? []);

        return Error.Validation("pagination.invalid_sort", $"The sort is not valid. Sortable fields: {allowedNames}.") with
        {
            Parameters = new Dictionary<string, object?> { ["allowed"] = allowedNames },
        };
    }
}
