namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>Where the current request comes from. Every value is null outside a request.</summary>
internal interface IClientContext
{
    /// <summary>The client address, after the forwarded-headers middleware has applied trusted proxies.</summary>
    string? IpAddress { get; }

    string? UserAgent { get; }

    /// <summary>The W3C trace id, the same value as the <c>X-Trace-Id</c> response header.</summary>
    string? TraceId { get; }
}
