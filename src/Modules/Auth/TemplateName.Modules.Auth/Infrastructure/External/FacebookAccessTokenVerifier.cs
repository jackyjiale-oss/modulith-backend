using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Infrastructure.External;

/// <summary>
/// Verifies a Facebook user access token through the Graph API (spec 5.2), on the <c>facebook-graph</c> client and its resilience
/// pipeline. Two calls, both <c>GET</c> on <c>https://graph.facebook.com</c>: <c>/debug_token</c> (the token is inspected with the app
/// access token <c>{AppId}|{AppSecret}</c>) must show <c>is_valid</c> true, <c>type</c> <c>USER</c> when stated, an <c>app_id</c> equal to
/// the provider's <c>AppId</c>, a <c>user_id</c> and an <c>expires_at</c> that has not passed (<c>0</c> means the token never expires);
/// then <c>/me?fields=id,name,email</c> (with <c>appsecret_proof</c>, the lower-case hex HMAC-SHA256 of the token keyed by the app secret)
/// whose <c>id</c> must equal that <c>user_id</c>. The subject is the Facebook user ID. Facebook's email is never verified, so
/// <see cref="VerifiedExternalIdentity.HasUsableEmail"/> is always false; the email is kept for display only.
/// <para>
/// How a Graph answer is read: an invalid, expired or foreign token is <see cref="ExternalLoginErrors.InvalidToken"/> (Graph answers
/// <c>200</c> with <c>is_valid</c> false, and the reason inside <c>data</c>, for <c>debug_token</c>); a <c>5xx</c>, a timeout, a network
/// error, an open breaker, an oversized or unparseable body, or a body that is not an object of the documented shape is
/// <see cref="ExternalLoginErrors.ProviderUnavailable"/>. A 4xx is told apart as far as Graph allows: on <c>/me</c> a 4xx whose
/// <c>error.code</c> is <c>190</c> (the user's token is invalid or revoked) is <c>InvalidToken</c>, any other 4xx is
/// <c>ProviderUnavailable</c>; on <c>debug_token</c> every non-2xx answer is <c>ProviderUnavailable</c>, because Graph reports a problem of the
/// user's token inside <c>data</c> and a top-level error there means our own app credentials (or the app) were refused, which is never the
/// user's fault and is logged for the operator (status and error code only). Nothing else is guessed from the text of an error.
/// </para>
/// <para>
/// The access token, the app secret and the proof reach no log, exception text or <c>ToString</c>: the token and the secret are sent in the
/// query string of the two calls (Graph's documented form), so nothing here logs a request address, a body or an exception message, only
/// the operation, the provider name, the exception's type name, the HTTP status and Graph's numeric error code. There is no replay guard: an
/// access token carries no nonce to spend (a documented limit; the token's own expiry and Facebook's revocation bound its use). It never
/// throws for an input (a cancelled caller still throws).
/// </para>
/// </summary>
internal sealed partial class FacebookAccessTokenVerifier(
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger<FacebookAccessTokenVerifier> logger)
{
    private const string GraphBase = "https://graph.facebook.com";

    // Both answers are a few hundred bytes; a larger body is refused unread (HttpClient throws once the buffer would exceed this).
    private const int MaxResponseBytes = 64 * 1024;

    // The error code of Graph's "invalid OAuth access token" (expired, revoked, malformed or signed for another app).
    private const int InvalidAccessTokenErrorCode = 190;

    // A duplicated member is refused, so no claim can be read two ways.
    private static readonly JsonDocumentOptions BodyOptions = new() { AllowDuplicateProperties = false, MaxDepth = 16 };

    public async Task<Result<VerifiedExternalIdentity>> VerifyAsync(ExternalProvider provider, string accessToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (provider.Kind != ExternalProviderKind.Facebook || !IsPlausible(accessToken))
        {
            return ExternalLoginErrors.InvalidToken;
        }

        var appId = provider.AppId;
        var appSecret = provider.AppSecret;
        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(appSecret))
        {
            throw new InvalidOperationException($"The external provider '{provider.Name}' has no app ID or app secret to call the Graph API with.");
        }

        var escapedToken = Uri.EscapeDataString(accessToken);

        var debugAddress = $"{GraphBase}/debug_token?input_token={escapedToken}&access_token={Uri.EscapeDataString($"{appId}|{appSecret}")}";
        using var debugReply = await GetAsync(provider, "debug_token", debugAddress, cancellationToken);
        if (debugReply is null)
        {
            return ExternalLoginErrors.ProviderUnavailable;
        }

        if (!debugReply.IsSuccess)
        {
            LogRefused(logger, "debug_token", provider.Name, (int)debugReply.Status, debugReply.ErrorCode);
            return ExternalLoginErrors.ProviderUnavailable;
        }

        if (debugReply.Body is not { ValueKind: JsonValueKind.Object } debugRoot
            || !debugRoot.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            LogUnexpectedShape(logger, "debug_token", provider.Name);
            return ExternalLoginErrors.ProviderUnavailable;
        }

        if (!TryReadDebug(provider, data, timeProvider.GetUtcNow(), out var userId))
        {
            return ExternalLoginErrors.InvalidToken;
        }

        var proof = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), Encoding.UTF8.GetBytes(accessToken)));
        var meAddress = $"{GraphBase}/me?fields=id,name,email&access_token={escapedToken}&appsecret_proof={proof}";
        using var meReply = await GetAsync(provider, "me", meAddress, cancellationToken);
        if (meReply is null)
        {
            return ExternalLoginErrors.ProviderUnavailable;
        }

        if (!meReply.IsSuccess)
        {
            if (meReply.IsClientError && meReply.ErrorCode == InvalidAccessTokenErrorCode)
            {
                return ExternalLoginErrors.InvalidToken;
            }

            LogRefused(logger, "me", provider.Name, (int)meReply.Status, meReply.ErrorCode);
            return ExternalLoginErrors.ProviderUnavailable;
        }

        if (meReply.Body is not { ValueKind: JsonValueKind.Object } me)
        {
            LogUnexpectedShape(logger, "me", provider.Name);
            return ExternalLoginErrors.ProviderUnavailable;
        }

        // The token must belong to the user debug_token named: /me answers for whoever the token is.
        if (!TryGetId(me, "id", out var meId) || !string.Equals(meId, userId, StringComparison.Ordinal))
        {
            return ExternalLoginErrors.InvalidToken;
        }

        return new VerifiedExternalIdentity(provider.Name, userId, EmailOf(me), HasUsableEmail: false, NameOf(me));
    }

    /// <summary>
    /// Whether <c>debug_token</c>'s <paramref name="data"/> vouches for the token: valid, a user token of this app, not expired, naming a user.
    /// Anything missing or of another type fails, so a changed answer never lets a token through.
    /// </summary>
    private static bool TryReadDebug(ExternalProvider provider, JsonElement data, DateTimeOffset now, out string userId)
    {
        userId = string.Empty;

        // Exactly the boolean true: Graph says is_valid false (and the reason inside data) for an invalid or expired token.
        if (!data.TryGetProperty("is_valid", out var isValid) || isValid.ValueKind != JsonValueKind.True)
        {
            return false;
        }

        if (data.TryGetProperty("type", out var type)
            && (type.ValueKind != JsonValueKind.String || !string.Equals(type.GetString(), "USER", StringComparison.Ordinal)))
        {
            return false;
        }

        if (!TryGetId(data, "app_id", out var appId) || !string.Equals(appId, provider.AppId, StringComparison.Ordinal))
        {
            return false;
        }

        // 0 is a token that never expires; a negative or fractional value is not a time; anything else must lie after now.
        if (!data.TryGetProperty("expires_at", out var expiresAt) || expiresAt.ValueKind != JsonValueKind.Number
            || !expiresAt.TryGetInt64(out var expiry) || expiry < 0
            || (expiry != 0 && expiry <= now.ToUnixTimeSeconds()))
        {
            return false;
        }

        if (!TryGetId(data, "user_id", out var id))
        {
            return false;
        }

        userId = id;
        return true;
    }

    /// <summary>
    /// A token Facebook could have issued: at most <see cref="ExternalProof.MaxTokenLength"/> characters, every one printable ASCII (no
    /// space, control or non-ASCII character), checked before any request.
    /// </summary>
    private static bool IsPlausible(string? accessToken) =>
        accessToken is { Length: > 0 and <= ExternalProof.MaxTokenLength }
        && accessToken.All(character => character is > ' ' and < (char)127);

    /// <summary>
    /// A Facebook ID: a string (or a whole JSON number, read as its digits) of 1 to 256 characters with no white space or control character.
    /// </summary>
    private static bool TryGetId(JsonElement parent, string name, out string id)
    {
        id = string.Empty;
        if (!parent.TryGetProperty(name, out var element))
        {
            return false;
        }

        var value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
        if (string.IsNullOrEmpty(value) || value.Length > UserExternalLogin.SubjectMaxLength
            || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            return false;
        }

        id = value;
        return true;
    }

    /// <summary>The trimmed <c>email</c>, or null when it is absent, not a string, blank or longer than the column.</summary>
    private static string? EmailOf(JsonElement me)
    {
        if (!me.TryGetProperty("email", out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var email = element.GetString()!.Trim();
        return email.Length is > 0 and <= UserExternalLogin.EmailMaxLength ? email : null;
    }

    /// <summary>The trimmed <c>name</c>, or null when it is absent, not a string or blank.</summary>
    private static string? NameOf(JsonElement me) =>
        me.TryGetProperty("name", out var element) && element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString())
            ? element.GetString()!.Trim()
            : null;

    /// <summary>
    /// One <c>GET</c>. Null when no answer could be had (a network error, a timeout, an open breaker, a body over the limit; logged by
    /// type name only); otherwise the status and the body when it is a JSON document. A cancelled caller still throws.
    /// </summary>
    private async Task<GraphReply?> GetAsync(ExternalProvider provider, string operation, string address, CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(ExternalHttpClients.FacebookGraph);
            client.MaxResponseContentBufferSize = MaxResponseBytes;
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(address));
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return new GraphReply(response.StatusCode, Parse(body));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Never the message: it may carry the request address, and with it the token.
            LogFailed(logger, operation, provider.Name, (exception.InnerException ?? exception).GetType().Name);
            return null;
        }
    }

    private static JsonDocument? Parse(byte[] body)
    {
        try
        {
            return JsonDocument.Parse(body, BodyOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Graph API call {Operation} for external provider {Provider} failed ({ExceptionType}); the sign-in was refused as unavailable")]
    private static partial void LogFailed(ILogger logger, string operation, string provider, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Graph API refused {Operation} for external provider {Provider} with HTTP {Status} and error code {ErrorCode}; the sign-in was refused as unavailable")]
    private static partial void LogRefused(ILogger logger, string operation, string provider, int status, int? errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Graph API answered {Operation} for external provider {Provider} in a shape that is not documented; the sign-in was refused as unavailable")]
    private static partial void LogUnexpectedShape(ILogger logger, string operation, string provider);

    /// <summary>A Graph answer: the status and, when it is JSON, the body (which the owner disposes).</summary>
    private sealed class GraphReply(HttpStatusCode status, JsonDocument? body) : IDisposable
    {
        public HttpStatusCode Status => status;

        public bool IsSuccess => (int)status is >= 200 and < 300;

        public bool IsClientError => (int)status is >= 400 and < 500;

        /// <summary>The root of the body, null when the body is not JSON.</summary>
        public JsonElement? Body => body?.RootElement;

        /// <summary>Graph's numeric <c>error.code</c> of an error answer, null when there is none.</summary>
        public int? ErrorCode =>
            Body is { ValueKind: JsonValueKind.Object } root
            && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value)
                ? value
                : null;

        public void Dispose() => body?.Dispose();
    }
}
