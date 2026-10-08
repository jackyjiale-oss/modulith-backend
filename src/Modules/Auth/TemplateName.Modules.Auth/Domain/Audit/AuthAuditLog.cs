using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Audit;

/// <summary>
/// One line of the append-only authentication and administration audit log. It is not an aggregate: it has no domain events and its
/// key is a database identity. The client information (address, user agent, trace id) is filled in by the writer from the request.
/// </summary>
internal sealed class AuthAuditLog : Entity<long>
{
    /// <summary>The column limit of <see cref="AttemptedIdentifier"/>.</summary>
    public const int MaxAttemptedIdentifierLength = 256;

    /// <summary>The column limit of <see cref="IpAddress"/> (long enough for IPv6 with an IPv4 tail).</summary>
    public const int MaxIpAddressLength = 45;

    /// <summary>The column limit of <see cref="UserAgent"/>.</summary>
    public const int MaxUserAgentLength = 512;

    /// <summary>The column limit of <see cref="TraceId"/>.</summary>
    public const int MaxTraceIdLength = 64;

    private const string Mask = "****";

    // EF Core materializes the entity through this constructor; callers use Create.
    private AuthAuditLog()
    {
    }

    public DateTimeOffset OccurredAt { get; private set; }

    public Guid? UserId { get; private set; }

    /// <summary>One of <see cref="AuthAuditEvents"/>.</summary>
    public string EventType { get; private set; } = string.Empty;

    public bool Succeeded { get; private set; }

    /// <summary>The error code or reason of a failure.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>What the caller typed to identify themselves, masked (see <see cref="MaskIdentifier"/>), for events without a known user.</summary>
    public string? AttemptedIdentifier { get; private set; }

    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public Guid? SessionId { get; private set; }

    public string? TraceId { get; private set; }

    /// <summary>Free-form JSON text with extra facts; the domain neither parses nor bounds it.</summary>
    public string? Details { get; private set; }

    public static AuthAuditLog Create(
        string eventType,
        bool succeeded,
        DateTimeOffset now,
        Guid? userId = null,
        string? failureReason = null,
        string? attemptedIdentifier = null,
        Guid? sessionId = null,
        string? details = null)
    {
        return new AuthAuditLog
        {
            OccurredAt = now,
            UserId = userId,
            EventType = eventType,
            Succeeded = succeeded,
            FailureReason = failureReason,
            AttemptedIdentifier = Truncate(attemptedIdentifier, MaxAttemptedIdentifierLength),
            SessionId = sessionId,
            Details = details,
        };
    }

    /// <summary>Masks an identifier for the log: an email keeps its first character and domain, anything else keeps its first character.</summary>
    public static string? MaskIdentifier(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var at = trimmed.IndexOf('@', StringComparison.Ordinal);

        return at <= 0
            ? string.Concat(trimmed.AsSpan(0, 1), Mask)
            : string.Concat(trimmed.AsSpan(0, 1), Mask, trimmed.AsSpan(at));
    }

    /// <summary>Records where the request came from; values longer than their column are cut.</summary>
    internal void SetClientInfo(string? ipAddress, string? userAgent, string? traceId)
    {
        IpAddress = Truncate(ipAddress, MaxIpAddressLength);
        UserAgent = Truncate(userAgent, MaxUserAgentLength);
        TraceId = Truncate(traceId, MaxTraceIdLength);
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is { } text && text.Length > maxLength ? text[..maxLength] : value;
}
