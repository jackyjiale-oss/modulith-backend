using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// The whole life of two-step verification for one user, through the real host, database, outbox and delivery worker (only the SMTP
/// transport is <c>RecordingEmailSender</c>): sign in on two sessions, enrol, sign in with a challenge and a trusted device, step up,
/// regenerate the recovery codes, sign in and step up with recovery codes, disable, sign in with the password alone, and read the
/// emails the changes sent, in the user's language.
/// </summary>
public sealed class MfaFlowTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string LoginRoute = "/api/v1/auth/login";
    private const string LogoutRoute = "/api/v1/auth/logout";
    private const string RefreshRoute = "/api/v1/auth/token/refresh";
    private const string SetupRoute = "/api/v1/auth/mfa/totp/setup";
    private const string ConfirmRoute = "/api/v1/auth/mfa/totp/confirm";
    private const string VerifyRoute = "/api/v1/auth/mfa/verify";
    private const string MethodsRoute = "/api/v1/auth/mfa/methods";
    private const string RegenerateRoute = "/api/v1/auth/mfa/recovery-codes/regenerate";
    private const string StepUpRoute = "/api/v1/auth/step-up";
    private const string Email = "alice@example.com";
    private const string DeviceName = "Alice's laptop";
    private const string MfaChangedSubject = "您的两步验证设置已更改";
    private const string NewDeviceSubject = "您的账户有新的登录";

    private static readonly TimeSpan NextTimeStep = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Enroll_sign_in_step_up_trust_and_recover()
    {
        var user = await CreateUserAsync(Email, locale: "zh-Hans");

        // Two sign-ins on one device: two sessions, and the second is not a new device.
        var first = await SignInAsync(Email, DeviceName);
        var second = await SignInAsync(Email, DeviceName);
        first.SessionId.ShouldNotBe(second.SessionId);

        // Enrolment from the first session: its fresh auth_time is the step-up the setup and the confirm need.
        AuthTestHarness.Authorize(Client, first.AccessToken);
        var secret = await SetUpAsync();
        Factory.Time.Advance(NextTimeStep);
        var recoveryCodes = await ConfirmAsync(secret);
        recoveryCodes.Count.ShouldBe(RecoveryCodeFormat.Count);

        // Confirming ended the other session and kept this one.
        using (var refused = await Client.PostAsJsonAsync(RefreshRoute, new { refreshToken = second.RefreshToken }, Ct))
        {
            await AssertProblemAsync(refused, HttpStatusCode.Unauthorized, "auth.invalid_refresh_token");
        }

        using (var kept = await Client.PostAsJsonAsync(RefreshRoute, new { refreshToken = first.RefreshToken }, Ct))
        {
            kept.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var logout = await Client.PostAsync(LogoutRoute, content: null, Ct))
        {
            logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // The password alone now gets a challenge; a code and rememberDevice trade it for tokens and a device token.
        AuthTestHarness.Authorize(Client, accessToken: null);
        var challenge = await ChallengeAsync();
        Factory.Time.Advance(NextTimeStep);
        var verified = await VerifyAsync(challenge, new { code = CodeAt(secret), rememberDevice = true });
        Amr(verified.AccessToken).ShouldBe(["pwd", "otp", "mfa"]);
        var deviceToken = verified.TrustedDeviceToken.ShouldNotBeNull();

        // The remembered device signs in with the password alone (amr pwd), and a code steps that session up.
        var trusted = await SignInAsync(Email, DeviceName, deviceToken);
        Amr(new JsonWebToken(trusted.AccessToken)).ShouldBe(["pwd"]);
        AuthTestHarness.Authorize(Client, trusted.AccessToken);
        Factory.Time.Advance(NextTimeStep);
        var steppedUp = await StepUpAsync(new { code = CodeAt(secret) });
        Amr(steppedUp).ShouldBe(["otp"]);
        AuthTestHarness.Authorize(Client, steppedUp.EncodedToken);

        // New recovery codes: the old ones are dead and the device is forgotten (the security stamp rotated).
        var regenerated = await RegenerateAsync();
        regenerated.Intersect(recoveryCodes, StringComparer.Ordinal).ShouldBeEmpty();
        AuthTestHarness.Authorize(Client, accessToken: null);
        var afterRegeneration = await ChallengeAsync(deviceToken);

        // A new recovery code finishes that sign-in.
        var recovered = await VerifyAsync(afterRegeneration, new { recoveryCode = regenerated[0] });
        Amr(recovered.AccessToken).ShouldBe(["pwd", "rcvr", "mfa"]);
        recovered.TrustedDeviceToken.ShouldBeNull();

        // Another one steps up, and with that step-up MFA is switched off.
        AuthTestHarness.Authorize(Client, recovered.AccessToken.EncodedToken);
        var recoveryStepUp = await StepUpAsync(new { recoveryCode = regenerated[1] });
        Amr(recoveryStepUp).ShouldBe(["rcvr"]);
        AuthTestHarness.Authorize(Client, recoveryStepUp.EncodedToken);
        var methodId = (await GetMethodsAsync()).GetProperty("methods").EnumerateArray().ShouldHaveSingleItem().GetProperty("id").GetGuid();
        using (var disable = await Client.DeleteAsync($"{MethodsRoute}/{methodId}", Ct))
        {
            disable.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // With MFA off the password alone signs in, and no method or code is left.
        AuthTestHarness.Authorize(Client, accessToken: null);
        var plain = await SignInAsync(Email, DeviceName);
        Amr(new JsonWebToken(plain.AccessToken)).ShouldBe(["pwd"]);
        (await QueryAsync(context => context.Set<UserMfaMethod>().CountAsync(Ct))).ShouldBe(0);
        (await QueryAsync(context => context.Set<RecoveryCode>().CountAsync(Ct))).ShouldBe(0);
        (await QueryAsync(context => context.Set<User>().AsNoTracking().SingleAsync(candidate => candidate.Id == user.Id, Ct))).Locale.ShouldBe("zh-Hans");

        // Real outbox and delivery: one email per MFA change, in the user's language, and none about a new device.
        await DispatchNotificationsAsync();

        var emails = Factory.EmailSender.Sent.Where(message => message.To == Email).ToList();
        emails.ShouldAllBe(message => message.Subject == MfaChangedSubject);
        emails.Count.ShouldBe(5);
        emails.Count(message => message.TextBody.Contains("您的账户已开启两步验证。", StringComparison.Ordinal)).ShouldBe(1);
        emails.Count(message => message.TextBody.Contains("已生成新的恢复码", StringComparison.Ordinal)).ShouldBe(1);
        emails.Count(message => message.TextBody.Contains("已使用恢复码登录您的账户。", StringComparison.Ordinal)).ShouldBe(2);
        emails.Count(message => message.TextBody.Contains("您的账户已关闭两步验证。", StringComparison.Ordinal)).ShouldBe(1);
        Factory.EmailSender.Sent.ShouldNotContain(message => message.Subject == NewDeviceSubject);
        Factory.IntegrationEvents.Events.OfType<NewDeviceSignedInIntegrationEvent>().ShouldBeEmpty();
        Factory.IntegrationEvents.Events.OfType<MfaChangedIntegrationEvent>().Select(integrationEvent => integrationEvent.Change).ShouldBe(
            [
                MfaChangeKind.Enabled,
                MfaChangeKind.RecoveryCodesRegenerated,
                MfaChangeKind.RecoveryCodeUsed,
                MfaChangeKind.RecoveryCodeUsed,
                MfaChangeKind.Disabled,
            ],
            ignoreOrder: true);
    }

    private static List<string> Amr(JsonWebToken token) => [.. token.Claims.Where(claim => claim.Type == "amr").Select(claim => claim.Value)];

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe(code);
    }

    private string CodeAt(byte[] secret) => Totp.ComputeCode(secret, Totp.TimeStepAt(Factory.Time.GetUtcNow()));

    /// <summary>A password sign-in that answers tokens (no challenge); a trusted device token may come with it.</summary>
    private async Task<Tokens> SignInAsync(string email, string deviceName, string? trustedDeviceToken = null)
    {
        using var response = await Client.PostAsJsonAsync(LoginRoute, new { email, password = AuthTestHarness.Password, deviceName, trustedDeviceToken }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("mfaRequired").GetBoolean().ShouldBeFalse();
        return new Tokens(
            body.GetProperty("accessToken").GetString()!,
            body.GetProperty("refreshToken").GetString()!,
            body.GetProperty("sessionId").GetGuid());
    }

    /// <summary>The password step of an MFA account (a stale or unknown device token may come with it); returns the challenge token.</summary>
    private async Task<string> ChallengeAsync(string? trustedDeviceToken = null)
    {
        using var response = await Client.PostAsJsonAsync(
            LoginRoute,
            new { email = Email, password = AuthTestHarness.Password, deviceName = DeviceName, trustedDeviceToken },
            Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("mfaRequired").GetBoolean().ShouldBeTrue();
        return body.GetProperty("challengeToken").GetString()!;
    }

    private async Task<Verified> VerifyAsync(string challengeToken, object proof)
    {
        var body = JsonSerializer.SerializeToElement(proof);
        var request = new Dictionary<string, object?> { ["challengeToken"] = challengeToken };
        foreach (var property in body.EnumerateObject())
        {
            request[property.Name] = property.Value;
        }

        using var response = await Client.PostAsJsonAsync(VerifyRoute, request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tokens = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        tokens.TryGetProperty("trustedDeviceToken", out var device);
        return new Verified(new JsonWebToken(tokens.GetProperty("accessToken").GetString()), device.ValueKind == JsonValueKind.String ? device.GetString() : null);
    }

    private async Task<byte[]> SetUpAsync()
    {
        using var setup = await Client.PostAsJsonAsync(SetupRoute, new { }, Ct);
        setup.StatusCode.ShouldBe(HttpStatusCode.OK);
        return Base32.Decode((await setup.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("secret").GetString()!);
    }

    private async Task<List<string>> ConfirmAsync(byte[] secret)
    {
        using var confirm = await Client.PostAsJsonAsync(ConfirmRoute, new { code = CodeAt(secret) }, Ct);
        confirm.StatusCode.ShouldBe(HttpStatusCode.OK);
        return CodesOf(await confirm.Content.ReadFromJsonAsync<JsonElement>(Ct));
    }

    private async Task<List<string>> RegenerateAsync()
    {
        using var regenerate = await Client.PostAsync(RegenerateRoute, content: null, Ct);
        regenerate.StatusCode.ShouldBe(HttpStatusCode.OK);
        return CodesOf(await regenerate.Content.ReadFromJsonAsync<JsonElement>(Ct));
    }

    private static List<string> CodesOf(JsonElement body)
        => [.. body.GetProperty("recoveryCodes").EnumerateArray().Select(item => item.GetString()!)];

    private async Task<JsonWebToken> StepUpAsync(object proof)
    {
        using var response = await Client.PostAsJsonAsync(StepUpRoute, proof, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return new JsonWebToken((await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString());
    }

    private async Task<JsonElement> GetMethodsAsync()
    {
        using var response = await Client.GetAsync(MethodsRoute, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<T> QueryAsync<T>(Func<AuthDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    private sealed record Tokens(string AccessToken, string RefreshToken, Guid SessionId);

    private sealed record Verified(JsonWebToken AccessToken, string? TrustedDeviceToken);
}
