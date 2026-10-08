using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using TemplateName.Modules.Auth.Application.Abstractions;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

/// <summary>Reads the client of the current HTTP request. Outside a request every value is null.</summary>
internal sealed class HttpClientContext(IHttpContextAccessor httpContextAccessor) : IClientContext
{
    public string? IpAddress
    {
        get
        {
            var address = httpContextAccessor.HttpContext?.Connection.RemoteIpAddress;

            return address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4().ToString() : address?.ToString();
        }
    }

    public string? UserAgent
        => httpContextAccessor.HttpContext?.Request.Headers.UserAgent.ToString() is { Length: > 0 } userAgent ? userAgent : null;

    // The same value as the X-Trace-Id response header (TraceIdHeaderMiddleware).
    public string? TraceId
        => Activity.Current?.TraceId.ToHexString() ?? httpContextAccessor.HttpContext?.TraceIdentifier;
}
