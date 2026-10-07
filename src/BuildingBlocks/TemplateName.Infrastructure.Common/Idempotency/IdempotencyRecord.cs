namespace TemplateName.Infrastructure.Common.Idempotency;

/// <summary>
/// An <c>Idempotency-Key</c> a caller has used and the response it got (a row of <c>platform.IdempotencyKeys</c>). A null
/// <see cref="StatusCode"/> means the first request with the key is still running.
/// </summary>
internal sealed class IdempotencyRecord
{
    internal const int MaxScopeLength = 100;
    internal const int MaxKeyLength = 100;
    internal const int RequestHashLength = 32;
    internal const int MaxContentTypeLength = 255;
    internal const int MaxLocationLength = 2048;

    /// <summary>Whose key it is: the signed-in user's id, or <c>anonymous</c>.</summary>
    public required string Scope { get; init; }

    public required string Key { get; init; }

    /// <summary>SHA-256 of the request's method, path and body.</summary>
    public required byte[] RequestHash { get; init; }

    public int? StatusCode { get; init; }

    public string? ContentType { get; init; }

    public byte[]? ResponseBody { get; init; }

    public string? Location { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime ExpiresAt { get; init; }
}
