using TemplateName.SharedKernel;

namespace TemplateName.Application.Common.Concurrency;

/// <summary>The errors of <c>If-Match</c> preconditions. Their messages are in <c>CommonErrorMessages</c> (Web.Common) and its translations.</summary>
public static class ConcurrencyErrors
{
    /// <summary>412: the resource changed since the caller read the version it sent.</summary>
    public static readonly Error PreconditionFailed = Error.PreconditionFailed(
        "concurrency.precondition_failed",
        "The resource was changed since you read it. Read it again and retry with its current ETag.");

    /// <summary>400: the <c>If-Match</c> header cannot be read (a client bug, not a stale copy).</summary>
    public static readonly Error MalformedIfMatch = Error.Validation("request.malformed", "The If-Match header is malformed.");
}
