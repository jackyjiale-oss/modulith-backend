using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using TemplateName.Modules.Notifications.Application.Abstractions;

namespace TemplateName.Modules.Notifications.Infrastructure.Channels.Push;

/// <summary>
/// <see cref="IPushTransport"/> over FCM HTTP v1: <c>POST https://fcm.googleapis.com/v1/projects/{project}/messages:send</c> through
/// the named client <see cref="ClientName"/> (its resilience pipeline of the same name; a POST is not retried inside an attempt unless
/// that pipeline sets <c>RetryUnsafeMethods</c>, so a lost answer cannot send twice there), with the bearer token of
/// <see cref="IFcmAccessTokenSource"/>. A failure is classified by the FCM <c>errorCode</c> in the answer's details, else by the status
/// alone; only <c>UNREGISTERED</c> deactivates the device, because a sender mismatch or a status without a code (a 403 or 404) may be
/// the server's own configuration rather than the token. The answer's body is never logged or attached: it can quote the token.
/// </summary>
internal sealed partial class FcmPushTransport(
    IHttpClientFactory httpClientFactory,
    IFcmAccessTokenSource tokens,
    IOptions<FcmOptions> options,
    ILogger<FcmPushTransport> logger) : IPushTransport
{
    /// <summary>The named client and the name of its resilience pipeline.</summary>
    internal const string ClientName = "push-provider";

    private const string FcmErrorType = "google.firebase.fcm.v1.FcmError";

    public async Task SendAsync(PushMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var typeCode = message.Data.TryGetValue("typeCode", out var code) ? code : string.Empty;
        try
        {
            await SendCoreAsync(message, cancellationToken);
            LogSent(logger, message.DeliveryId, message.DeviceId, typeCode);
        }
        catch (PushSendException failure)
        {
            LogFailed(logger, message.DeliveryId, message.DeviceId, typeCode, failure.Reason, failure.IsPermanent);
            throw;
        }
    }

    private async Task SendCoreAsync(PushMessage message, CancellationToken cancellationToken)
    {
        var accessToken = await tokens.GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(options.Value.ProjectId)}/messages:send"))
        {
            Content = JsonContent.Create(Body(message)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(ClientName).SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new PushSendException(isPermanent: false, "connection_failed", deactivateDevice: false);
        }
        catch (TimeoutRejectedException)
        {
            throw new PushSendException(isPermanent: false, "timeout", deactivateDevice: false);
        }
        catch (BrokenCircuitException)
        {
            throw new PushSendException(isPermanent: false, "circuit_open", deactivateDevice: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PushSendException(isPermanent: false, "timeout", deactivateDevice: false);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            throw Classify(response.StatusCode, await ErrorCodeAsync(response, cancellationToken));
        }
    }

    private static object Body(PushMessage message) => new
    {
        message = new
        {
            token = message.Token,
            notification = new { title = message.Title, body = message.Body },
            data = message.Data,
            android = new { priority = message.Urgent ? "HIGH" : "NORMAL" },
            apns = new { headers = new Dictionary<string, string> { ["apns-priority"] = message.Urgent ? "10" : "5" } },
        },
    };

    private static PushSendException Classify(HttpStatusCode status, string? errorCode) => errorCode switch
    {
        "UNREGISTERED" => new PushSendException(isPermanent: true, "fcm_unregistered", deactivateDevice: true),
        // Most often the server's own Firebase project is wrong (staging and production mixed up): deactivating would silence every
        // user's devices, and fixing the configuration would not bring them back. The device is kept; the Warning names the reason.
        "SENDER_ID_MISMATCH" => new PushSendException(isPermanent: true, "fcm_sender_id_mismatch", deactivateDevice: false),
        "INVALID_ARGUMENT" => new PushSendException(isPermanent: true, "fcm_invalid_argument", deactivateDevice: false),
        "QUOTA_EXCEEDED" => new PushSendException(isPermanent: false, "fcm_quota_exceeded", deactivateDevice: false),
        "UNAVAILABLE" => new PushSendException(isPermanent: false, "fcm_unavailable", deactivateDevice: false),
        "INTERNAL" => new PushSendException(isPermanent: false, "fcm_internal", deactivateDevice: false),
        "THIRD_PARTY_AUTH_ERROR" => new PushSendException(isPermanent: false, "fcm_third_party_auth_error", deactivateDevice: false),
        _ => new PushSendException(IsPermanentStatus(status), $"fcm_http_{(int)status}", deactivateDevice: false),
    };

    // 401, 403 (configuration), 408 and 429 (slow down) and 5xx are worth another attempt; any other 4xx is not.
    private static bool IsPermanentStatus(HttpStatusCode status)
        => (int)status is >= 400 and < 500 && status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);

    // The errorCode of the FcmError detail, or null when the answer has none or is not JSON (a proxy's HTML page).
    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object
                || !error.TryGetProperty("details", out var details)
                || details.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var detail in details.EnumerateArray())
            {
                // Every value is checked for its kind first: GetString throws on a number or an object (a proxy's own JSON).
                if (detail.ValueKind == JsonValueKind.Object
                    && detail.TryGetProperty("@type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && type.GetString()!.EndsWith(FcmErrorType, StringComparison.Ordinal)
                    && detail.TryGetProperty("errorCode", out var errorCode)
                    && errorCode.ValueKind == JsonValueKind.String)
                {
                    return errorCode.GetString();
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Push accepted by FCM for delivery {DeliveryId} to device {DeviceId} ({TypeCode})")]
    private static partial void LogSent(ILogger logger, Guid deliveryId, Guid deviceId, string typeCode);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Push refused for delivery {DeliveryId} to device {DeviceId} ({TypeCode}): {Reason}, permanent {IsPermanent}")]
    private static partial void LogFailed(ILogger logger, Guid deliveryId, Guid deviceId, string typeCode, string reason, bool isPermanent);
}
