using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Audit;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

/// <summary>
/// Reads the client of the current HTTP request. Outside a request every value is null. Values are cut to the audit log columns
/// (<see cref="AuthAuditLog"/>), because the client controls the <c>User-Agent</c> header and an IPv6 zone id can take an address past
/// 45 characters.
/// </summary>
internal sealed class HttpClientContext(IHttpContextAccessor httpContextAccessor) : IClientContext
{
    public string? IpAddress
    {
        get
        {
            var address = httpContextAccessor.HttpContext?.Connection.RemoteIpAddress;
            var text = address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4().ToString() : address?.ToString();

            return Truncate(text, AuthAuditLog.MaxIpAddressLength);
        }
    }

    public string? UserAgent
        => httpContextAccessor.HttpContext?.Request.Headers.UserAgent.ToString() is { Length: > 0 } userAgent
            ? Truncate(userAgent, AuthAuditLog.MaxUserAgentLength)
            : null;

    // The same value as the X-Trace-Id response header (TraceIdHeaderMiddleware).
    public string? TraceId
        => Truncate(
            Activity.Current?.TraceId.ToHexString() ?? httpContextAccessor.HttpContext?.TraceIdentifier,
            AuthAuditLog.MaxTraceIdLength);

    private static string? Truncate(string? value, int maxLength) =>
        value is { } text && text.Length > maxLength ? text[..maxLength] : value;
}
