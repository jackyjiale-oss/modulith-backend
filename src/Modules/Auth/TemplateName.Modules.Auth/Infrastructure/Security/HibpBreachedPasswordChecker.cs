using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Passwords;

namespace TemplateName.Modules.Auth.Infrastructure.Security;

/// <summary>
/// Checks a password against Have I Been Pwned with k-anonymity: only the first five characters of its SHA-1 leave the process,
/// and the match is made here against the returned suffixes. Any network failure answers "not breached" and logs a warning.
/// </summary>
internal sealed partial class HibpBreachedPasswordChecker(
    IHttpClientFactory httpClientFactory,
    IOptions<PasswordOptions> options,
    ILogger<HibpBreachedPasswordChecker> logger) : IBreachedPasswordChecker
{
    /// <summary>The name of the <see cref="HttpClient"/> the factory builds for this checker.</summary>
    internal const string HttpClientName = "hibp";

    private const string RangeUrl = "https://api.pwnedpasswords.com/range/";
    private const int PrefixLength = 5;

    public async Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken)
    {
        if (!options.Value.CheckBreached)
        {
            return false;
        }

        // The k-anonymity protocol of the service is defined on SHA-1; it is not used here to protect anything.
#pragma warning disable CA5350
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
#pragma warning restore CA5350
        var prefix = hash[..PrefixLength];
        var suffix = hash[PrefixLength..];

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, RangeUrl + prefix);
            request.Headers.Add("Add-Padding", "true");

            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogNonSuccess(logger, (int)response.StatusCode);
                return false;
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(body);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (IsListed(line, suffix))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException
            && !cancellationToken.IsCancellationRequested)
        {
            // The caller's own cancellation is not caught: only a timeout or a transport error fails open. The exception text
            // is not logged, so nothing about the request can reach the log.
            LogFailed(logger, exception.GetType().Name);
            return false;
        }
    }

    /// <summary>A row is <c>SUFFIX:COUNT</c>; padding rows have a count of 0 and are not real hits.</summary>
    private static bool IsListed(string line, string suffix)
    {
        var separator = line.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            return false;
        }

        return line.AsSpan(0, separator).Equals(suffix, StringComparison.OrdinalIgnoreCase)
            && long.TryParse(line.AsSpan(separator + 1), out var count)
            && count > 0;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The breached-password service answered with status {StatusCode}; the check was skipped")]
    private static partial void LogNonSuccess(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The breached-password check failed with {ExceptionType}; the check was skipped")]
    private static partial void LogFailed(ILogger logger, string exceptionType);
}
