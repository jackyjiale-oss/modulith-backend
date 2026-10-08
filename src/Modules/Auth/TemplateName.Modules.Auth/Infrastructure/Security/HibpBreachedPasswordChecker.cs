using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Passwords;

namespace TemplateName.Modules.Auth.Infrastructure.Security;

/// <summary>
/// Checks a password against Have I Been Pwned with k-anonymity: only the first five characters of its SHA-1 leave the process,
/// and the match is made here against the returned suffixes. The whole lookup (request, headers and body) has one time budget and
/// the body one size cap; any failure, timeout or excess answers "not breached" and logs a warning.
/// </summary>
internal sealed partial class HibpBreachedPasswordChecker : IBreachedPasswordChecker
{
    /// <summary>The name of the <see cref="HttpClient"/> the factory builds for this checker.</summary>
    internal const string HttpClientName = "hibp";

    /// <summary>The most a padded range answer holds is some 40 KB; a body past this cap is not from the service.</summary>
    internal const int MaxBodyBytes = 1024 * 1024;

    private const string RangeUrl = "https://api.pwnedpasswords.com/range/";
    private const int PrefixLength = 5;
    private const int ReadChunkBytes = 8 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<PasswordOptions> _options;
    private readonly ILogger<HibpBreachedPasswordChecker> _logger;
    private readonly TimeSpan _budget;
    private readonly int _maxBodyBytes;

    public HibpBreachedPasswordChecker(
        IHttpClientFactory httpClientFactory,
        IOptions<PasswordOptions> options,
        ILogger<HibpBreachedPasswordChecker> logger)
        : this(httpClientFactory, options, logger, RequestBudget, MaxBodyBytes)
    {
    }

    /// <summary>Takes the budget and the cap so a test does not have to wait the real time or build a real megabyte.</summary>
    internal HibpBreachedPasswordChecker(
        IHttpClientFactory httpClientFactory,
        IOptions<PasswordOptions> options,
        ILogger<HibpBreachedPasswordChecker> logger,
        TimeSpan budget,
        int maxBodyBytes)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
        _budget = budget;
        _maxBodyBytes = maxBodyBytes;
    }

    /// <summary>The time the whole lookup may take, also the timeout of the named client.</summary>
    internal static TimeSpan RequestBudget { get; } = TimeSpan.FromSeconds(2);

    public async Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken)
    {
        if (!_options.Value.CheckBreached)
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
            // One budget for the request, the headers and the body: the client timeout alone ends when the headers arrive.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(_budget);

            using var request = new HttpRequestMessage(HttpMethod.Get, RangeUrl + prefix);
            request.Headers.Add("Add-Padding", "true");

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            if (!response.IsSuccessStatusCode)
            {
                LogNonSuccess(_logger, (int)response.StatusCode);
                return false;
            }

            await using var body = await response.Content.ReadAsStreamAsync(budget.Token);
            var text = await ReadBoundedAsync(body, budget.Token);
            if (text is null)
            {
                LogBodyTooLarge(_logger, _maxBodyBytes);
                return false;
            }

            foreach (var line in text.AsSpan().EnumerateLines())
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
            // The caller's own cancellation is not caught: only the budget running out or a transport error fails open. The
            // exception text is not logged, so nothing about the request can reach the log.
            LogFailed(_logger, exception.GetType().Name);
            return false;
        }
    }

    /// <summary>A row is <c>SUFFIX:COUNT</c>; padding rows have a count of 0 and are not real hits.</summary>
    private static bool IsListed(ReadOnlySpan<char> line, string suffix)
    {
        var separator = line.IndexOf(':');
        if (separator < 0)
        {
            return false;
        }

        return line[..separator].Equals(suffix, StringComparison.OrdinalIgnoreCase)
            && long.TryParse(line[(separator + 1)..], out var count)
            && count > 0;
    }

    /// <summary>Reads the body as text, or returns null as soon as it would pass the cap (nothing past the cap is kept).</summary>
    private async Task<string?> ReadBoundedAsync(Stream body, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream();
        var chunk = new byte[ReadChunkBytes];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (content.Length + read > _maxBodyBytes)
            {
                return null;
            }

            content.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(content.GetBuffer(), 0, (int)content.Length);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The breached-password service answered with status {StatusCode}; the check was skipped")]
    private static partial void LogNonSuccess(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The breached-password check failed with {ExceptionType}; the check was skipped")]
    private static partial void LogFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The breached-password service answered with more than {MaxBodyBytes} bytes; the check was skipped")]
    private static partial void LogBodyTooLarge(ILogger logger, int maxBodyBytes);
}
