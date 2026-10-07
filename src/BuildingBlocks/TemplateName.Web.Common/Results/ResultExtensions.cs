using Microsoft.AspNetCore.Http;
using TemplateName.SharedKernel;

namespace TemplateName.Web.Common.Results;

public static class ResultExtensions
{
    /// <summary>Maps a failed <see cref="Result"/> to an RFC 9457 problem response (status per <see cref="ErrorType"/>).</summary>
    /// <exception cref="InvalidOperationException">The result is a success.</exception>
    public static IResult ToProblem(this Result result)
    {
        if (result.IsSuccess)
        {
            throw new InvalidOperationException("A successful result cannot be converted to a problem response.");
        }

        var error = result.Error;
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };

        if (error is ValidationError validationError)
        {
            return TypedResults.ValidationProblem(
                new Dictionary<string, string[]>(validationError.Errors),
                detail: error.Message,
                extensions: extensions);
        }

        return TypedResults.Problem(
            statusCode: ToStatusCode(error.Type),
            detail: error.Message,
            extensions: extensions);
    }

    private static int ToStatusCode(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError,
    };
}
