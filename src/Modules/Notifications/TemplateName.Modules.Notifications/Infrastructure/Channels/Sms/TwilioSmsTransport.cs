using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using TemplateName.Modules.Notifications.Application.Abstractions;

namespace TemplateName.Modules.Notifications.Infrastructure.Channels.Sms;

/// <summary>
/// <see cref="ISmsTransport"/> over Twilio's Messages API: <c>POST https://api.twilio.com/2010-04-01/Accounts/{AccountSid}/Messages.json</c>
/// with a form (<c>To</c>, <c>Body</c>, and <c>From</c> or <c>MessagingServiceSid</c>) and Basic authentication, through the named client
/// <see cref="ClientName"/> (its resilience pipeline of the same name, which does not retry the POST, so a lost answer cannot text twice
/// there). A failure is classified by Twilio's numeric error <c>code</c> when the answer is Twilio's JSON, else by the status alone; a
/// configuration fault (the credentials, the sender) is transient, so the deliveries wait for the fix. The answer's body is never
/// logged or attached: it can quote the number.
/// </summary>
internal sealed partial class TwilioSmsTransport(
    IHttpClientFactory httpClientFactory,
    IOptions<TwilioOptions> options,
    ILogger<TwilioSmsTransport> logger) : ISmsTransport
{
    /// <summary>The named client and the name of its resilience pipeline.</summary>
    internal const string ClientName = "sms-provider";

    // The recipient: invalid number, not a mobile, region not enabled, replied STOP. No retry can fix these.
    private static readonly HashSet<int> RecipientCodes = [21211, 21614, 21408, 21610];

    // The server's own configuration: bad credentials, a sender that is not ours, no sender. Transient: fixing it lets them through.
    private static readonly HashSet<int> ConfigurationCodes = [20003, 21606, 21659];

    public async Task<string?> SendAsync(SmsMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            var messageId = await SendCoreAsync(message, cancellationToken);
            if (messageId is null)
            {
                LogSentWithoutId(logger, message.DeliveryId);
            }
            else
            {
                LogSent(logger, message.DeliveryId);
            }

            return messageId;
        }
        catch (SmsSendException failure)
        {
            LogFailed(logger, message.DeliveryId, failure.Reason, failure.IsPermanent);
            throw;
        }
    }

    private async Task<string?> SendCoreAsync(SmsMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var form = new List<KeyValuePair<string, string>> { new("To", message.To), new("Body", message.Body) };
        form.Add(string.IsNullOrWhiteSpace(settings.MessagingServiceSid)
            ? new("From", settings.From)
            : new("MessagingServiceSid", settings.MessagingServiceSid));
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"https://api.twilio.com/2010-04-01/Accounts/{Uri.EscapeDataString(settings.AccountSid)}/Messages.json"))
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.AccountSid}:{settings.AuthToken}")));

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(ClientName).SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new SmsSendException(isPermanent: false, "connection_failed");
        }
        catch (TimeoutRejectedException)
        {
            throw new SmsSendException(isPermanent: false, "timeout");
        }
        catch (BrokenCircuitException)
        {
            throw new SmsSendException(isPermanent: false, "circuit_open");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SmsSendException(isPermanent: false, "timeout");
        }

        using (response)
        {
            using var document = await ParseAsync(response, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return MessageIdOf(document);
            }

            throw Classify(response.StatusCode, ErrorCodeOf(document));
        }
    }

    private static SmsSendException Classify(HttpStatusCode status, int? code)
    {
        if (code is { } recipient && RecipientCodes.Contains(recipient))
        {
            return new SmsSendException(isPermanent: true, $"twilio_{recipient}");
        }

        if (code is { } configuration && ConfigurationCodes.Contains(configuration))
        {
            return new SmsSendException(isPermanent: false, $"twilio_{configuration}");
        }

        return new SmsSendException(IsPermanentStatus(status), $"twilio_http_{(int)status}");
    }

    // 401 (configuration), 408 and 429 (slow down) and 5xx are worth another attempt; any other 4xx is not.
    private static bool IsPermanentStatus(HttpStatusCode status)
        => (int)status is >= 400 and < 500
            && status is not (HttpStatusCode.Unauthorized or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);

    // The answer as JSON, or null when it is not JSON (a proxy's HTML page).
    private static async Task<JsonDocument?> ParseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Twilio's numeric error code, only when the answer is an object whose "code" is an integer: a string or a fraction is not Twilio's.
    private static int? ErrorCodeOf(JsonDocument? document)
        => document is { RootElement.ValueKind: JsonValueKind.Object }
            && document.RootElement.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.Number
            && code.TryGetInt32(out var value)
                ? value
                : null;

    // The message SID, only in its documented form (SM or MM and 32 lowercase hex digits), so nothing else reaches the column or a log.
    private static string? MessageIdOf(JsonDocument? document)
        => document is { RootElement.ValueKind: JsonValueKind.Object }
            && document.RootElement.TryGetProperty("sid", out var sid)
            && sid.ValueKind == JsonValueKind.String
            && sid.GetString() is { } value
            && MessageSidPattern().IsMatch(value)
                ? value
                : null;

    [GeneratedRegex(@"^(SM|MM)[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex MessageSidPattern();

    [LoggerMessage(Level = LogLevel.Information, Message = "SMS accepted by Twilio for delivery {DeliveryId}")]
    private static partial void LogSent(ILogger logger, Guid deliveryId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SMS accepted by Twilio for delivery {DeliveryId}, but the answer held no usable message SID")]
    private static partial void LogSentWithoutId(ILogger logger, Guid deliveryId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SMS refused for delivery {DeliveryId}: {Reason}, permanent {IsPermanent}")]
    private static partial void LogFailed(ILogger logger, Guid deliveryId, string reason, bool isPermanent);
}
