using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TemplateName.Modules.Auth;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.External;
using TemplateName.Modules.Auth.Infrastructure.Tokens;
using TemplateName.SharedKernel;
using TemplateName.UnitTests.Infrastructure.Http;

namespace TemplateName.UnitTests.Auth.External;

/// <summary>
/// The Facebook path of <see cref="IExternalIdentityVerifier"/> as the module registers it: an access token judged through the Graph API
/// (<c>debug_token</c>, then <c>/me</c>) on the <c>facebook-graph</c> client and its pipeline, and an iOS Limited Login ID token judged by
/// the OIDC verifier (audience: the app ID, discovery at Facebook's address with its trailing slash), against a scripted Graph and
/// <see cref="FakeIdentityProvider"/>, on fake time. Every log entry of the whole pipeline is captured to prove no secret reaches it.
/// </summary>
public sealed class FacebookAccessTokenVerifierTests
{
    // Years away from the real clock, so a check that read the system clock instead of the TimeProvider would fail.
    private static readonly DateTimeOffset Now = new(2031, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private const string AppId = "1234567890123456";
    private const string AppSecret = FakeIdentityProvider.DefaultFacebookAppSecret;
    private const string AccessToken = "EAAB1x2y3zFakeUserAccessToken0123456789ZDabc";
    private const string UserId = "10158812345678901";
    private const string UserName = "Alice Example";
    private const string UserEmail = "alice@example.com";
    private const string LimitedLoginSubject = "limited-login-subject-0001";
    private const string FacebookIssuer = "https://www.facebook.com";

    [Fact]
    public async Task A_valid_token_yields_the_facebook_id_as_subject_and_never_a_usable_email()
    {
        using var host = new Host();

        var result = await host.VerifyAsync();

        var identity = Succeeded(result);
        identity.Provider.ShouldBe("facebook");
        identity.Subject.ShouldBe(UserId);
        identity.Name.ShouldBe(UserName);
        identity.Email.ShouldBe(UserEmail);
        identity.HasUsableEmail.ShouldBeFalse();
    }

    [Fact]
    public async Task Graph_is_asked_debug_token_with_the_app_access_token_and_then_me_with_the_proof()
    {
        using var host = new Host();

        Succeeded(await host.VerifyAsync());

        var requests = host.Graph.Handler.Requests.ToArray();
        requests.Length.ShouldBe(2);
        requests.ShouldAllBe(request => request.Method == HttpMethod.Get);

        var debug = requests[0].Uri;
        debug.GetLeftPart(UriPartial.Path).ShouldBe("https://graph.facebook.com/debug_token");
        var debugQuery = HttpUtility.ParseQueryString(debug.Query);
        debugQuery.AllKeys.OrderBy(key => key, StringComparer.Ordinal).ShouldBe(["access_token", "input_token"]);
        debugQuery["input_token"].ShouldBe(AccessToken);
        debugQuery["access_token"].ShouldBe($"{AppId}|{AppSecret}");

        var me = requests[1].Uri;
        me.GetLeftPart(UriPartial.Path).ShouldBe("https://graph.facebook.com/me");
        var meQuery = HttpUtility.ParseQueryString(me.Query);
        meQuery.AllKeys.OrderBy(key => key, StringComparer.Ordinal).ShouldBe(["access_token", "appsecret_proof", "fields"]);
        meQuery["fields"].ShouldBe("id,name,email");
        meQuery["access_token"].ShouldBe(AccessToken);
        meQuery["appsecret_proof"].ShouldBe(ExpectedProof());
    }

    [Fact]
    public async Task The_appsecret_proof_is_the_lower_case_hex_HMAC_SHA256_of_the_token_keyed_by_the_app_secret()
    {
        using var host = new Host();
        var otherToken = "EAAB-another-user-token-9876543210";
        host.Graph.ServeDebug(DebugBody());
        host.Graph.ServeMe(MeBody());

        Succeeded(await host.VerifyAsync(otherToken));

        var proof = HttpUtility.ParseQueryString(host.Graph.Handler.Requests.Last().Uri.Query)["appsecret_proof"];
        proof.ShouldBe(ExpectedProof(otherToken));
        proof.ShouldBe(proof!.ToLowerInvariant());
        proof.Length.ShouldBe(64);
    }

    [Fact]
    public async Task A_token_that_never_expires_is_accepted()
    {
        using var host = new Host();
        host.Graph.ServeDebug(DebugBody(data => data["expires_at"] = 0));

        Succeeded(await host.VerifyAsync());
    }

    [Fact]
    public async Task A_token_is_valid_until_the_second_it_expires_on_the_applications_clock()
    {
        using var host = new Host();
        var now = Now.ToUnixTimeSeconds();

        host.Graph.ServeDebug(DebugBody(data => data["expires_at"] = now + 1));
        Succeeded(await host.VerifyAsync());

        host.Graph.ServeDebug(DebugBody(data => data["expires_at"] = now));
        Failed(await host.VerifyAsync(), ExternalLoginErrors.InvalidToken);
    }

    [Fact]
    public async Task Ids_written_as_numbers_are_read_as_their_digits()
    {
        using var host = new Host();
        host.Graph.ServeDebug(DebugBody(data =>
        {
            data["app_id"] = long.Parse(AppId, System.Globalization.CultureInfo.InvariantCulture);
            data["user_id"] = long.Parse(UserId, System.Globalization.CultureInfo.InvariantCulture);
        }));

        var identity = Succeeded(await host.VerifyAsync());

        identity.Subject.ShouldBe(UserId);
    }

    [Fact]
    public async Task A_user_id_of_exactly_256_characters_is_the_subject()
    {
        using var host = new Host();
        var longest = new string('7', UserExternalLogin.SubjectMaxLength);
        host.Graph.ServeDebug(DebugBody(data => data["user_id"] = longest));
        host.Graph.ServeMe(MeBody(me => me["id"] = longest));

        var identity = Succeeded(await host.VerifyAsync());

        identity.Subject.ShouldBe(longest);
    }

    [Fact]
    public async Task A_missing_name_and_email_are_null_and_a_blank_one_too()
    {
        using var host = new Host();
        host.Graph.ServeMe(MeBody(me =>
        {
            me.Remove("name");
            me.Remove("email");
        }));
        var missing = Succeeded(await host.VerifyAsync());

        host.Graph.ServeMe(MeBody(me =>
        {
            me["name"] = "   ";
            me["email"] = "  ";
        }));
        var blank = Succeeded(await host.VerifyAsync());

        foreach (var identity in new[] { missing, blank })
        {
            identity.Name.ShouldBeNull();
            identity.Email.ShouldBeNull();
            identity.HasUsableEmail.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task The_name_and_the_email_are_trimmed_and_an_email_over_256_characters_is_dropped()
    {
        using var host = new Host();
        host.Graph.ServeMe(MeBody(me =>
        {
            me["name"] = "  Alice Example  ";
            me["email"] = "  alice@example.com ";
        }));
        var trimmed = Succeeded(await host.VerifyAsync());

        var tooLong = new string('a', UserExternalLogin.EmailMaxLength) + "@example.com";
        host.Graph.ServeMe(MeBody(me => me["email"] = tooLong));
        var dropped = Succeeded(await host.VerifyAsync());

        trimmed.Name.ShouldBe("Alice Example");
        trimmed.Email.ShouldBe("alice@example.com");
        dropped.Email.ShouldBeNull();
        dropped.HasUsableEmail.ShouldBeFalse();
    }

    [Theory]
    [InlineData("is-valid-false")]
    [InlineData("is-valid-missing")]
    [InlineData("is-valid-as-a-string")]
    [InlineData("is-valid-as-a-number")]
    [InlineData("another-app")]
    [InlineData("app-id-with-whitespace")]
    [InlineData("app-id-missing")]
    [InlineData("app-id-as-an-object")]
    [InlineData("expired")]
    [InlineData("expires-exactly-now")]
    [InlineData("expiry-missing")]
    [InlineData("expiry-as-a-string")]
    [InlineData("expiry-negative")]
    [InlineData("expiry-fractional")]
    [InlineData("user-id-missing")]
    [InlineData("user-id-empty")]
    [InlineData("user-id-blank")]
    [InlineData("user-id-over-256-characters")]
    [InlineData("user-id-as-an-object")]
    [InlineData("a-page-token")]
    [InlineData("an-app-token")]
    public async Task A_token_Graph_does_not_vouch_for_is_InvalidToken_and_me_is_never_asked(string case_)
    {
        using var host = new Host();
        host.Graph.ServeDebug(DebugBodyFor(case_));

        var result = await host.VerifyAsync();

        Failed(result, ExternalLoginErrors.InvalidToken);
        host.Graph.Handler.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData("a-different-id")]
    [InlineData("no-id")]
    [InlineData("an-empty-id")]
    [InlineData("an-id-as-an-object")]
    [InlineData("an-id-in-another-form")]
    public async Task A_me_answer_for_another_user_is_InvalidToken(string case_)
    {
        using var host = new Host();
        host.Graph.ServeMe(MeBody(me =>
        {
            switch (case_)
            {
                case "a-different-id":
                    me["id"] = "10158812345678902";
                    break;
                case "no-id":
                    me.Remove("id");
                    break;
                case "an-empty-id":
                    me["id"] = string.Empty;
                    break;
                case "an-id-as-an-object":
                    me["id"] = new JsonObject { ["id"] = UserId };
                    break;
                case "an-id-in-another-form":
                    me["id"] = " " + UserId;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(case_), case_, null);
            }
        }));

        Failed(await host.VerifyAsync(), ExternalLoginErrors.InvalidToken);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task A_me_answer_that_says_the_user_token_is_invalid_is_InvalidToken(HttpStatusCode status)
    {
        using var host = new Host();
        host.Graph.ServeMe(GraphError(code: 190, subcode: 460), status);

        Failed(await host.VerifyAsync(), ExternalLoginErrors.InvalidToken);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 100)]
    [InlineData(HttpStatusCode.BadRequest, 10)]
    [InlineData(HttpStatusCode.Forbidden, 200)]
    [InlineData(HttpStatusCode.BadRequest, 0)]
    public async Task A_me_error_that_is_not_about_the_user_token_is_ProviderUnavailable(HttpStatusCode status, int code)
    {
        using var host = new Host();
        host.Graph.ServeMe(GraphError(code), status);

        Failed(await host.RunAsync(host.VerifyAsync()), ExternalLoginErrors.ProviderUnavailable);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_debug_token_error_is_about_the_app_token_not_the_user_and_is_ProviderUnavailable(HttpStatusCode status)
    {
        // Graph reports a problem of the user's token inside data (is_valid false); a top-level error means our app credentials were refused.
        using var host = new Host();
        host.Graph.ServeDebug(GraphError(code: 190), status);

        var result = await host.RunAsync(host.VerifyAsync());

        Failed(result, ExternalLoginErrors.ProviderUnavailable);
        host.Graph.Handler.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_Graph_server_error_on_debug_token_is_ProviderUnavailable(HttpStatusCode status)
    {
        using var host = new Host();
        host.Graph.ServeDebug("{}", status);

        Failed(await host.RunAsync(host.VerifyAsync()), ExternalLoginErrors.ProviderUnavailable);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_Graph_server_error_on_me_is_ProviderUnavailable(HttpStatusCode status)
    {
        using var host = new Host();
        host.Graph.ServeMe("{}", status);

        Failed(await host.RunAsync(host.VerifyAsync()), ExternalLoginErrors.ProviderUnavailable);
    }

    [Fact]
    public async Task A_network_error_on_either_call_is_ProviderUnavailable()
    {
        using var host = new Host();
        host.Graph.ThrowOnDebug();
        Failed(await host.RunAsync(host.VerifyAsync()), ExternalLoginErrors.ProviderUnavailable);

        host.Graph.ServeDebug(DebugBody());
        host.Graph.ThrowOnMe();
        Failed(await host.RunAsync(host.VerifyAsync()), ExternalLoginErrors.ProviderUnavailable);
    }

    [Fact]
    public async Task A_Graph_that_never_answers_debug_token_is_ProviderUnavailable()
    {
        using var host = new Host();
        host.Graph.HangDebug();

        Failed(await host.RunAsync(host.VerifyAsync()), ExternalLoginErrors.ProviderUnavailable);
    }

    [Fact]
    public async Task A_Graph_that_never_answers_me_is_ProviderUnavailable()
    {
        using var host = new Host();
        host.Graph.HangMe();

        Failed(await host.RunAsync(host.VerifyAsync()), ExternalLoginErrors.ProviderUnavailable);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{}")]
    [InlineData("{\"data\":\"text\"}")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":{\"is_valid\":true,\"is_valid\":false}}")]
    public async Task A_debug_token_body_that_is_not_the_documented_shape_is_ProviderUnavailable(string body)
    {
        using var host = new Host();
        host.Graph.ServeDebug(body);

        Failed(await host.VerifyAsync(), ExternalLoginErrors.ProviderUnavailable);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{\"id\":\"1\",\"id\":\"2\"}")]
    public async Task A_me_body_that_is_not_an_object_is_ProviderUnavailable(string body)
    {
        using var host = new Host();
        host.Graph.ServeMe(body);

        Failed(await host.VerifyAsync(), ExternalLoginErrors.ProviderUnavailable);
    }

    [Fact]
    public async Task An_oversized_Graph_body_is_ProviderUnavailable()
    {
        using var host = new Host();
        host.Graph.ServeDebug(DebugBody(data => data["padding"] = new string('x', 100_000)));

        Failed(await host.VerifyAsync(), ExternalLoginErrors.ProviderUnavailable);
        host.Graph.Handler.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("with a space")]
    [InlineData("tab\there")]
    [InlineData("new\nline")]
    [InlineData("naïve-token")]
    [InlineData("delete\u007ftoken")]
    public async Task A_token_that_cannot_be_one_is_InvalidToken_without_calling_Graph(string token)
    {
        using var host = new Host();

        Failed(await host.VerifyAsync(token), ExternalLoginErrors.InvalidToken);
        host.Graph.Handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_token_over_8192_characters_is_InvalidToken_without_calling_Graph()
    {
        using var host = new Host();

        Failed(await host.VerifyAsync(new string('A', ExternalProof.MaxTokenLength + 1)), ExternalLoginErrors.InvalidToken);
        host.Graph.Handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_token_with_characters_that_need_escaping_is_sent_escaped()
    {
        using var host = new Host();
        const string Token = "EAAB+/=&?#%token";

        Succeeded(await host.VerifyAsync(Token));

        var debug = host.Graph.Handler.Requests.First().Uri;
        debug.Query.ShouldContain("input_token=" + Uri.EscapeDataString(Token) + "&");
        HttpUtility.ParseQueryString(debug.Query)["input_token"].ShouldBe(Token);
    }

    [Fact]
    public async Task An_access_token_for_a_provider_that_is_not_Facebook_is_InvalidToken_without_calling_Graph()
    {
        using var host = new Host();

        var result = await host.VerifyAsync(provider: host.Idp.ToProvider("google"));

        Failed(result, ExternalLoginErrors.InvalidToken);
        host.Graph.Handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_cancelled_caller_gets_the_cancellation_not_ProviderUnavailable()
    {
        using var host = new Host();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            host.Verifier.VerifyAsync(host.Provider, new ExternalProof(null, AccessToken, null), cancelled.Token));
    }

    [Fact]
    public async Task An_access_token_has_no_nonce_so_it_is_not_spent_and_verifies_again()
    {
        // The documented limit: no replay guard for an access token (it carries no nonce to spend).
        using var host = new Host();

        Succeeded(await host.VerifyAsync());
        Succeeded(await host.VerifyAsync());
    }

    [Theory]
    [InlineData("success")]
    [InlineData("invalid-token")]
    [InlineData("expired-token")]
    [InlineData("debug-token-server-error")]
    [InlineData("me-server-error")]
    [InlineData("network-error")]
    [InlineData("timeout")]
    [InlineData("me-user-token-error")]
    public async Task Neither_the_access_token_nor_the_app_secret_reaches_a_log(string scenario)
    {
        using var host = new Host();
        switch (scenario)
        {
            case "success":
                break;
            case "invalid-token":
                host.Graph.ServeDebug(DebugBody(data => data["is_valid"] = false));
                break;
            case "expired-token":
                host.Graph.ServeDebug(DebugBody(data => data["expires_at"] = Now.ToUnixTimeSeconds() - 5));
                break;
            case "debug-token-server-error":
                host.Graph.ServeDebug("{}", HttpStatusCode.InternalServerError);
                break;
            case "me-server-error":
                host.Graph.ServeMe("{}", HttpStatusCode.BadGateway);
                break;
            case "network-error":
                host.Graph.ThrowOnDebug();
                break;
            case "timeout":
                host.Graph.HangMe();
                break;
            case "me-user-token-error":
                host.Graph.ServeMe(GraphError(code: 190), HttpStatusCode.BadRequest);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        _ = await host.RunAsync(host.VerifyAsync());

        host.Logs.Entries.ShouldContain(entry => entry.Category.StartsWith("System.Net.Http.HttpClient.facebook-graph", StringComparison.Ordinal), "the HttpClient pipeline logged nothing, so this test proves nothing");
        foreach (var secret in new[] { AccessToken, Uri.EscapeDataString(AccessToken), AppSecret, $"{AppId}|{AppSecret}", ExpectedProof() })
        {
            foreach (var entry in host.Logs.Entries)
            {
                entry.Text.ShouldNotContain(secret, Case.Sensitive, $"a log entry of {entry.Category} carries a secret");
            }
        }
    }

    [Fact]
    public async Task A_providers_to_string_never_carries_the_app_secret()
    {
        using var host = new Host();

        var result = await host.VerifyAsync();

        (host.Provider.ToString() ?? string.Empty).ShouldNotContain(AppSecret);
        (result.ToString() ?? string.Empty).ShouldNotContain(AccessToken);
    }

    [Fact]
    public async Task A_Limited_Login_ID_token_is_verified_with_the_app_id_as_audience_and_never_calls_Graph()
    {
        using var host = new Host();
        var token = host.LimitedLoginToken();

        var result = await host.VerifyIdTokenAsync(token);

        var identity = Succeeded(result);
        identity.Provider.ShouldBe("facebook");
        identity.Subject.ShouldBe(LimitedLoginSubject);
        identity.HasUsableEmail.ShouldBeFalse();
        host.Graph.Handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_Limited_Login_email_is_never_usable_even_when_the_token_says_verified()
    {
        using var host = new Host();
        var token = host.LimitedLoginToken(claims =>
        {
            claims["email"] = UserEmail;
            claims["email_verified"] = true;
        });

        var identity = Succeeded(await host.VerifyIdTokenAsync(token));

        identity.HasUsableEmail.ShouldBeFalse();
    }

    [Fact]
    public async Task Facebooks_discovery_document_is_read_at_its_address_with_the_trailing_slash()
    {
        using var host = new Host();

        Succeeded(await host.VerifyIdTokenAsync(host.LimitedLoginToken()));

        host.Idp.Handler.Requests.First().ShouldBe(new Uri("https://www.facebook.com/.well-known/openid-configuration/"));
        host.Idp.Handler.KeySetRequests.ShouldBe(1);
    }

    [Fact]
    public async Task A_verifier_that_dropped_the_trailing_slash_would_find_no_discovery_document()
    {
        // Guards the guard: the fake answers Facebook's slashed address only, so the test above fails if the slash goes missing.
        using var host = new Host();
        using var client = new HttpClient(host.Idp.Handler, disposeHandler: false);

        using var withSlash = await client.GetAsync(new Uri("https://www.facebook.com/.well-known/openid-configuration/"), TestContext.Current.CancellationToken);
        using var withoutSlash = await client.GetAsync(new Uri("https://www.facebook.com/.well-known/openid-configuration"), TestContext.Current.CancellationToken);

        withSlash.StatusCode.ShouldBe(HttpStatusCode.OK);
        withoutSlash.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_Limited_Login_token_for_another_app_is_InvalidToken()
    {
        using var host = new Host();
        var token = host.LimitedLoginToken(claims => claims["aud"] = "9999999999999999");

        Failed(await host.VerifyIdTokenAsync(token), ExternalLoginErrors.InvalidToken);
        host.Graph.Handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_Limited_Login_token_from_another_issuer_is_InvalidToken()
    {
        using var host = new Host();
        var token = host.LimitedLoginToken(claims => claims["iss"] = "https://facebook.com");

        Failed(await host.VerifyIdTokenAsync(token), ExternalLoginErrors.InvalidToken);
    }

    [Fact]
    public async Task A_Limited_Login_token_is_refused_under_another_nonce_and_accepted_only_once_under_its_own()
    {
        using var host = new Host();
        var token = host.LimitedLoginToken();

        Failed(await host.VerifyIdTokenAsync(token, nonce: "a-nonce-that-was-not-bound-0123"), ExternalLoginErrors.InvalidToken);
        Succeeded(await host.VerifyIdTokenAsync(token));
        Failed(await host.VerifyIdTokenAsync(token), ExternalLoginErrors.InvalidToken);
    }

    [Fact]
    public async Task A_Limited_Login_token_is_ProviderUnavailable_when_the_discovery_document_cannot_be_read()
    {
        using var host = new Host();
        host.Idp.Handler.FailWith(HttpStatusCode.InternalServerError);

        var result = await host.RunAsync(host.VerifyIdTokenAsync(host.LimitedLoginToken()));

        Failed(result, ExternalLoginErrors.ProviderUnavailable);
        host.Graph.Handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_proof_with_both_an_ID_token_and_an_access_token_is_InvalidToken_without_calling_Graph()
    {
        using var host = new Host();
        var proof = new ExternalProof(host.LimitedLoginToken(), AccessToken, FakeIdentityProvider.DefaultNonce);

        var result = await host.Verifier.VerifyAsync(host.Provider, proof, TestContext.Current.CancellationToken);

        Failed(result, ExternalLoginErrors.InvalidToken);
        host.Graph.Handler.Calls.ShouldBe(0);
    }

    private static VerifiedExternalIdentity Succeeded(Result<VerifiedExternalIdentity> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Code : null);
        return result.Value;
    }

    private static void Failed(Result<VerifiedExternalIdentity> result, Error expected)
    {
        result.IsFailure.ShouldBeTrue("the proof was accepted");
        result.Error.ShouldBe(expected);
    }

    private static string ExpectedProof(string accessToken = AccessToken) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(AppSecret), Encoding.UTF8.GetBytes(accessToken)));

    /// <summary>What Graph answers <c>debug_token</c> for a valid user token of <see cref="AppId"/>, changed by <paramref name="customize"/>.</summary>
    private static string DebugBody(Action<JsonObject>? customize = null)
    {
        var data = new JsonObject
        {
            ["app_id"] = AppId,
            ["type"] = "USER",
            ["application"] = "Example app",
            ["data_access_expires_at"] = Now.AddDays(90).ToUnixTimeSeconds(),
            ["expires_at"] = Now.AddDays(30).ToUnixTimeSeconds(),
            ["is_valid"] = true,
            ["scopes"] = new JsonArray("email", "public_profile"),
            ["user_id"] = UserId,
        };
        customize?.Invoke(data);
        return new JsonObject { ["data"] = data }.ToJsonString();
    }

    /// <summary>What Graph answers <c>/me?fields=id,name,email</c> for the user of <see cref="DebugBody"/>.</summary>
    private static string MeBody(Action<JsonObject>? customize = null)
    {
        var me = new JsonObject { ["id"] = UserId, ["name"] = UserName, ["email"] = UserEmail };
        customize?.Invoke(me);
        return me.ToJsonString();
    }

    private static string GraphError(int code, int? subcode = null)
    {
        var error = new JsonObject
        {
            ["message"] = "An error message of Graph",
            ["type"] = "OAuthException",
            ["code"] = code,
            ["fbtrace_id"] = "AbCdEf123456",
        };
        if (subcode is { } value)
        {
            error["error_subcode"] = value;
        }

        return new JsonObject { ["error"] = error }.ToJsonString();
    }

    private static string DebugBodyFor(string case_)
    {
        var now = Now.ToUnixTimeSeconds();
        return case_ switch
        {
            "is-valid-false" => new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["error"] = new JsonObject { ["code"] = 190, ["message"] = "Error validating access token: Session has expired", ["subcode"] = 463 },
                    ["is_valid"] = false,
                    ["scopes"] = new JsonArray(),
                },
            }.ToJsonString(),
            "is-valid-missing" => DebugBody(data => data.Remove("is_valid")),
            "is-valid-as-a-string" => DebugBody(data => data["is_valid"] = "true"),
            "is-valid-as-a-number" => DebugBody(data => data["is_valid"] = 1),
            "another-app" => DebugBody(data => data["app_id"] = "9999999999999999"),
            "app-id-with-whitespace" => DebugBody(data => data["app_id"] = " " + AppId),
            "app-id-missing" => DebugBody(data => data.Remove("app_id")),
            "app-id-as-an-object" => DebugBody(data => data["app_id"] = new JsonObject { ["id"] = AppId }),
            "expired" => DebugBody(data => data["expires_at"] = now - 1),
            "expires-exactly-now" => DebugBody(data => data["expires_at"] = now),
            "expiry-missing" => DebugBody(data => data.Remove("expires_at")),
            "expiry-as-a-string" => DebugBody(data => data["expires_at"] = (now + 3600).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            "expiry-negative" => DebugBody(data => data["expires_at"] = -5),
            "expiry-fractional" => DebugBody(data => data["expires_at"] = now + 3600.5),
            "user-id-missing" => DebugBody(data => data.Remove("user_id")),
            "user-id-empty" => DebugBody(data => data["user_id"] = string.Empty),
            "user-id-blank" => DebugBody(data => data["user_id"] = "   "),
            "user-id-over-256-characters" => DebugBody(data => data["user_id"] = new string('7', UserExternalLogin.SubjectMaxLength + 1)),
            "user-id-as-an-object" => DebugBody(data => data["user_id"] = new JsonObject { ["id"] = UserId }),
            "a-page-token" => DebugBody(data => data["type"] = "PAGE"),
            "an-app-token" => DebugBody(data =>
            {
                data["type"] = "APP";
                data.Remove("user_id");
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(case_), case_, null),
        };
    }

    /// <summary>A scripted Graph API: <c>/debug_token</c> and <c>/me</c> answer as set, valid for <see cref="AccessToken"/> by default.</summary>
    private sealed class FakeGraph
    {
        private volatile Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _debug = (_, _) => Reply(HttpStatusCode.OK, DebugBody());
        private volatile Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _me = (_, _) => Reply(HttpStatusCode.OK, MeBody());

        public FakeGraph() =>
            Handler = new ScriptedHttpMessageHandler((_, request, cancellationToken) => request.RequestUri!.AbsolutePath switch
            {
                "/debug_token" => _debug(request, cancellationToken),
                "/me" => _me(request, cancellationToken),
                _ => Reply(HttpStatusCode.NotFound, "{}"),
            });

        public ScriptedHttpMessageHandler Handler { get; }

        public void ServeDebug(string body, HttpStatusCode status = HttpStatusCode.OK) => _debug = (_, _) => Reply(status, body);

        public void ServeMe(string body, HttpStatusCode status = HttpStatusCode.OK) => _me = (_, _) => Reply(status, body);

        public void ThrowOnDebug() => _debug = (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("The network is down."));

        public void ThrowOnMe() => _me = (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("The network is down."));

        public void HangDebug() => _debug = HangAsync;

        public void HangMe() => _me = HangAsync;

        private static Task<HttpResponseMessage> Reply(HttpStatusCode status, string body) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

        private static async Task<HttpResponseMessage> HangAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /// <summary>
    /// Collects every entry of every logger, including the HttpClient pipeline's: the formatted message, the structured values, the
    /// exception and the scopes open at the time.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public IReadOnlyCollection<LogEntry> Entries => _entries;

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

        public void Dispose()
        {
        }

        private static void Append(StringBuilder text, object? value)
        {
            text.Append(value).Append('\n');
            if (value is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, pairValue) in pairs)
                {
                    text.Append(key).Append('=').Append(pairValue).Append('\n');
                }
            }
        }

        internal sealed record LogEntry(string Category, LogLevel Level, string Text);

        private sealed class CapturingLogger(string category, CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => owner._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var text = new StringBuilder();
                Append(text, formatter(state, exception));
                Append(text, state);
                if (exception is not null)
                {
                    Append(text, exception.ToString());
                }

                owner._scopes.ForEachScope((scope, builder) => Append(builder, scope), text);
                owner._entries.Enqueue(new LogEntry(category, logLevel, text.ToString()));
            }
        }
    }

    /// <summary>The module's external-login services over a scripted Graph and a fake OIDC provider at Facebook's address, on a fake clock starting at <see cref="Now"/>.</summary>
    private sealed class Host : IDisposable
    {
        private readonly ServiceProvider _services;

        public Host()
        {
            Time = new FakeTimeProvider(Now);
            Idp = new FakeIdentityProvider(Time, FacebookIssuer);
            Idp.Handler.ServeDiscoveryWithTrailingSlashOnly();
            Graph = new FakeGraph();
            Logs = new CapturingLoggerProvider();

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:External:MaxTokenAge"] = "00:05:00",
                ["Auth:Jwt:ClockSkew"] = "00:00:30",
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<IConfiguration>(configuration);
            services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.SectionName));
            AuthModule.AddPermissions(services, configuration);
            AuthModule.AddExternalLogin(services, configuration);
            services.AddHttpClient(ExternalHttpClients.OidcDiscovery).ConfigurePrimaryHttpMessageHandler(() => Idp.Handler);
            services.AddHttpClient(ExternalHttpClients.FacebookGraph).ConfigurePrimaryHttpMessageHandler(() => Graph.Handler);
            _services = services.BuildServiceProvider();
            Verifier = _services.GetRequiredService<IExternalIdentityVerifier>();
            Provider = Idp.ToProvider("facebook", clientId: AppId);
        }

        public FakeTimeProvider Time { get; }

        public FakeIdentityProvider Idp { get; }

        public FakeGraph Graph { get; }

        public CapturingLoggerProvider Logs { get; }

        public IExternalIdentityVerifier Verifier { get; }

        public ExternalProvider Provider { get; }

        public Task<Result<VerifiedExternalIdentity>> VerifyAsync(string accessToken = AccessToken, ExternalProvider? provider = null) =>
            Verifier.VerifyAsync(provider ?? Provider, new ExternalProof(null, accessToken, null), TestContext.Current.CancellationToken);

        public Task<Result<VerifiedExternalIdentity>> VerifyIdTokenAsync(string idToken, string? nonce = FakeIdentityProvider.DefaultNonce) =>
            Verifier.VerifyAsync(Provider, new ExternalProof(idToken, null, nonce), TestContext.Current.CancellationToken);

        /// <summary>An iOS Limited Login token: Facebook's issuer, the app ID as audience, a nonce and a <c>sub</c> that is not the Graph user id.</summary>
        public string LimitedLoginToken(Action<IDictionary<string, object>>? customize = null) =>
            Idp.IdToken(claims =>
            {
                claims["aud"] = AppId;
                claims["sub"] = LimitedLoginSubject;
                customize?.Invoke(claims);
            });

        /// <summary>Moves the fake clock while <paramref name="call"/> runs, so the pipeline's delays and timeouts elapse.</summary>
        public async Task<T> RunAsync<T>(Task<T> call)
        {
            var started = TimeProvider.System.GetTimestamp();
            while (!call.IsCompleted)
            {
                if (TimeProvider.System.GetElapsedTime(started) > TimeSpan.FromSeconds(20))
                {
                    throw new TimeoutException("The call did not complete while the fake time moved.");
                }

                Time.Advance(TimeSpan.FromMilliseconds(500));
                await Task.Delay(5);
            }

            return await call;
        }

        public void Dispose()
        {
            _services.Dispose();
            Idp.Dispose();
            Graph.Handler.Dispose();
        }
    }
}
