using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// Managing two-step verification through the running host: <c>DELETE /api/v1/auth/mfa/methods/{id}</c> and
/// <c>POST /api/v1/auth/mfa/recovery-codes/regenerate</c>, and the rule that changing the password of an MFA account needs a fresh
/// step-up. Every action on a confirmed method needs a step-up proved with a code (a TOTP code, or a recovery code when the device is
/// lost); a password is not enough.
/// </summary>
public sealed partial class MfaManagementTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string MethodsRoute = "/api/v1/auth/mfa/methods";
    private const string RegenerateRoute = "/api/v1/auth/mfa/recovery-codes/regenerate";
    private const string SetupRoute = "/api/v1/auth/mfa/totp/setup";
    private const string StepUpRoute = "/api/v1/auth/step-up";
    private const string LoginRoute = "/api/v1/auth/login";
    private const string RefreshRoute = "/api/v1/auth/token/refresh";
    private const string ChangePasswordRoute = "/api/v1/auth/password/change";
    private const string Email = "alice@example.com";
    private const string NewPassword = "a brand new passphrase";

    [Fact]
    public async Task Disable_with_a_fresh_totp_step_up_turns_mfa_off_and_login_no_longer_challenges()
    {
        var user = await CreateUserAsync(Email);
        var login = await LoginAsync();
        var enrollment = await EnrollTotpAsync(user.Id);
        var stampBefore = (await FindUserAsync(user.Id)).SecurityStamp;

        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        await StepUpAsync(login.AccessToken, new { code = enrollment.CodeAt(Factory.Time.GetUtcNow()) });

        using (var disable = await Client.DeleteAsync(MethodRoute(enrollment.MethodId), Ct))
        {
            disable.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // The method and every recovery code are gone; the stamp rotated; nothing else of the account changed.
        (await QueryAsync(context => context.Set<UserMfaMethod>().CountAsync(Ct))).ShouldBe(0);
        (await QueryAsync(context => context.Set<RecoveryCode>().CountAsync(Ct))).ShouldBe(0);
        (await FindUserAsync(user.Id)).SecurityStamp.ShouldNotBe(stampBefore);
        var audits = await QueryAsync(context => context.Set<AuthAuditLog>().AsNoTracking().OrderBy(entry => entry.Id).ToListAsync(Ct));
        audits.Select(entry => entry.EventType).ShouldBe([AuthAuditEvents.LoginSucceeded, AuthAuditEvents.StepUpSucceeded, AuthAuditEvents.MfaDisabled]);
        audits[2].Details.ShouldBe("""{"revokedSessionCount":0}""");
        audits[2].SessionId.ShouldBe(login.SessionId);
        var outbox = await QueryAsync(context => context.Set<OutboxMessage>().Select(message => message.Type).ToListAsync(Ct));
        outbox.Count(type => type.Contains(nameof(MfaChangedDomainEvent), StringComparison.Ordinal)).ShouldBe(1);

        var methods = await GetMethodsAsync();
        methods.GetProperty("enabled").GetBoolean().ShouldBeFalse();
        methods.GetProperty("methods").GetArrayLength().ShouldBe(0);

        // The password alone signs in again: no challenge.
        using var next = await Client.PostAsJsonAsync(LoginRoute, new { email = Email, password = AuthTestHarness.Password }, Ct);
        next.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await next.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("mfaRequired").GetBoolean().ShouldBeFalse();
        body.GetProperty("accessToken").GetString().ShouldNotBeNullOrEmpty();

        // The method is gone, so removing it again is a 404.
        using var again = await Client.DeleteAsync(MethodRoute(enrollment.MethodId), Ct);
        await AssertProblemAsync(again, HttpStatusCode.NotFound, "auth.mfa_method_not_found");
    }

    [Fact]
    public async Task Disable_with_a_password_step_up_is_refused()
    {
        // The token's amr is pwd: the password was the last thing proved, and a password does not step up an MFA account.
        var signedIn = await SignInAsync();
        var enrollment = await EnrollTotpAsync(signedIn.UserId);

        using (var refused = await Client.DeleteAsync(MethodRoute(enrollment.MethodId), Ct))
        {
            await AssertProblemAsync(refused, HttpStatusCode.Forbidden, "auth.step_up_required");
        }

        // Nor can the password be turned into a step-up: it is refused (and not counted).
        using (var password = await Client.PostAsJsonAsync(StepUpRoute, new { password = signedIn.Password }, Ct))
        {
            await AssertProblemAsync(password, HttpStatusCode.BadRequest, "auth.step_up_method_not_allowed");
        }

        using (var stillRefused = await Client.DeleteAsync(MethodRoute(enrollment.MethodId), Ct))
        {
            await AssertProblemAsync(stillRefused, HttpStatusCode.Forbidden, "auth.step_up_required");
        }

        (await QueryAsync(context => context.Set<UserMfaMethod>().CountAsync(Ct))).ShouldBe(1);
        (await QueryAsync(context => context.Set<RecoveryCode>().CountAsync(Ct))).ShouldBe(RecoveryCodeFormat.Count);
        (await FindUserAsync(signedIn.UserId)).AccessFailedCount.ShouldBe(0);
        (await QueryAsync(context => context.Set<AuthAuditLog>().CountAsync(entry => entry.EventType == AuthAuditEvents.MfaDisabled, Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Disable_after_the_step_up_has_aged_out_is_refused()
    {
        var user = await CreateUserAsync(Email);
        var login = await LoginAsync();
        var enrollment = await EnrollTotpAsync(user.Id);
        await StepUpAsync(login.AccessToken, new { code = enrollment.CodeAt(Factory.Time.GetUtcNow()) });

        Factory.Time.Advance(TimeSpan.FromMinutes(10));

        using var response = await Client.DeleteAsync(MethodRoute(enrollment.MethodId), Ct);
        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "auth.step_up_required");
        (await QueryAsync(context => context.Set<UserMfaMethod>().CountAsync(Ct))).ShouldBe(1);
    }

    [Fact]
    public async Task Lost_device_user_steps_up_with_a_recovery_code_and_disables_mfa()
    {
        var user = await CreateUserAsync(Email);
        var login = await LoginAsync();
        var enrollment = await EnrollTotpAsync(user.Id);

        // Typed the way people type it: lower case, no hyphens.
        var typed = enrollment.RecoveryCodes[2].Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        var token = await StepUpAsync(login.AccessToken, new { recoveryCode = typed });
        Amr(token).ShouldBe(["rcvr"]);

        using (var disable = await Client.DeleteAsync(MethodRoute(enrollment.MethodId), Ct))
        {
            disable.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await QueryAsync(context => context.Set<UserMfaMethod>().CountAsync(Ct))).ShouldBe(0);
        (await QueryAsync(context => context.Set<RecoveryCode>().CountAsync(Ct))).ShouldBe(0);
        var events = await QueryAsync(context => context.Set<AuthAuditLog>().OrderBy(entry => entry.Id).Select(entry => entry.EventType).ToListAsync(Ct));
        events.ShouldBe([AuthAuditEvents.LoginSucceeded, AuthAuditEvents.StepUpSucceeded, AuthAuditEvents.MfaDisabled]);

        using var next = await Client.PostAsJsonAsync(LoginRoute, new { email = Email, password = AuthTestHarness.Password }, Ct);
        (await next.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("mfaRequired").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Removing_a_pending_method_needs_no_step_up_and_changes_nothing_else()
    {
        var user = await CreateUserAsync(Email);
        var current = await LoginAsync();
        var other = await LoginAsync();
        AuthTestHarness.Authorize(Client, current.AccessToken);
        var stampBefore = (await FindUserAsync(user.Id)).SecurityStamp;
        using var setup = await Client.PostAsJsonAsync(SetupRoute, new { }, Ct);
        setup.StatusCode.ShouldBe(HttpStatusCode.OK);
        var methodId = (await setup.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("methodId").GetGuid();

        // The token's amr is only pwd: a pending setup protects nothing, so cancelling it needs nothing more.
        using (var cancel = await Client.DeleteAsync(MethodRoute(methodId), Ct))
        {
            cancel.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await QueryAsync(context => context.Set<UserMfaMethod>().CountAsync(Ct))).ShouldBe(0);
        (await FindUserAsync(user.Id)).SecurityStamp.ShouldBe(stampBefore);
        var events = await QueryAsync(context => context.Set<AuthAuditLog>().OrderBy(entry => entry.Id).Select(entry => entry.EventType).ToListAsync(Ct));
        events.ShouldNotContain(AuthAuditEvents.MfaDisabled);
        var outbox = await QueryAsync(context => context.Set<OutboxMessage>().Select(message => message.Type).ToListAsync(Ct));
        outbox.ShouldNotContain(type => type.Contains(nameof(MfaChangedDomainEvent), StringComparison.Ordinal));

        // Nobody was signed out.
        using var refresh = await Client.PostAsJsonAsync(RefreshRoute, new { refreshToken = other.RefreshToken }, Ct);
        refresh.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Another_users_method_and_an_unknown_one_get_the_same_404_whatever_the_step_up()
    {
        // The signed-in user has no step-up at all (a pwd token) and no method; another user has a confirmed one.
        await SignInAsync();
        var other = await CreateUserAsync("bob@example.com");
        var foreign = await EnrollTotpAsync(other.Id);
        var unknownId = Guid.NewGuid();

        using var foreignResponse = await Client.DeleteAsync(MethodRoute(foreign.MethodId), Ct);
        using var unknownResponse = await Client.DeleteAsync(MethodRoute(unknownId), Ct);

        await AssertProblemAsync(foreignResponse, HttpStatusCode.NotFound, "auth.mfa_method_not_found");
        await AssertProblemAsync(unknownResponse, HttpStatusCode.NotFound, "auth.mfa_method_not_found");
        var foreignBody = (await foreignResponse.Content.ReadAsStringAsync(Ct)).Replace(foreign.MethodId.ToString(), "{id}", StringComparison.OrdinalIgnoreCase);
        var unknownBody = (await unknownResponse.Content.ReadAsStringAsync(Ct)).Replace(unknownId.ToString(), "{id}", StringComparison.OrdinalIgnoreCase);
        TraceId().Replace(foreignBody, string.Empty).ShouldBe(TraceId().Replace(unknownBody, string.Empty));

        // The other user's method and codes are untouched.
        (await QueryAsync(context => context.Set<UserMfaMethod>().AnyAsync(method => method.Id == foreign.MethodId, Ct))).ShouldBeTrue();
        (await QueryAsync(context => context.Set<RecoveryCode>().CountAsync(Ct))).ShouldBe(RecoveryCodeFormat.Count);
    }

    [Fact]
    public async Task Old_recovery_codes_stop_working_after_regeneration()
    {
        var user = await CreateUserAsync(Email);
        var login = await LoginAsync();
        var enrollment = await EnrollTotpAsync(user.Id);
        await StepUpAsync(login.AccessToken, new { code = enrollment.CodeAt(Factory.Time.GetUtcNow()) });

        using var response = await Client.PostAsync(RegenerateRoute, content: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        response.Headers.Pragma.ToString().ShouldBe("no-cache");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.EnumerateObject().Select(property => property.Name).ShouldBe(["recoveryCodes"]);
        var fresh = body.GetProperty("recoveryCodes").EnumerateArray().Select(item => item.GetString()!).ToList();
        fresh.Count.ShouldBe(RecoveryCodeFormat.Count);
        fresh.ShouldAllBe(code => DisplayedRecoveryCode().IsMatch(code));
        fresh.Intersect(enrollment.RecoveryCodes, StringComparer.Ordinal).ShouldBeEmpty();

        // Exactly the new set is stored, as hashes: the old rows are gone.
        var stored = await QueryAsync(context => context.Set<RecoveryCode>().AsNoTracking().ToListAsync(Ct));
        stored.Select(code => Convert.ToHexString(code.CodeHash)).ShouldBe(
            fresh.Select(code => Convert.ToHexString(RecoveryCodeFormat.Hash(RecoveryCodeFormat.Normalize(code)!))),
            ignoreOrder: true);
        var methods = await GetMethodsAsync();
        methods.GetProperty("recoveryCodesRemaining").GetInt32().ShouldBe(RecoveryCodeFormat.Count);
        var audit = await QueryAsync(context => context.Set<AuthAuditLog>().SingleAsync(entry => entry.EventType == AuthAuditEvents.RecoveryCodesRegenerated, Ct));
        audit.Succeeded.ShouldBeTrue();
        audit.Details.ShouldBe("""{"revokedSessionCount":0}""");

        // An old code, used or not, no longer steps up (a wrong code, so a 400 that counts); a new one does, once.
        using (var old = await Client.PostAsJsonAsync(StepUpRoute, new { recoveryCode = enrollment.RecoveryCodes[0] }, Ct))
        {
            await AssertProblemAsync(old, HttpStatusCode.BadRequest, "auth.invalid_mfa_code");
        }

        using (var current = await Client.PostAsJsonAsync(StepUpRoute, new { recoveryCode = fresh[0] }, Ct))
        {
            current.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var reused = await Client.PostAsJsonAsync(StepUpRoute, new { recoveryCode = fresh[0] }, Ct);
        await AssertProblemAsync(reused, HttpStatusCode.BadRequest, "auth.invalid_mfa_code");
    }

    [Fact]
    public async Task Regeneration_ends_other_sessions_and_keeps_this_one()
    {
        var user = await CreateUserAsync(Email);
        var current = await LoginAsync();
        var other = await LoginAsync();
        var enrollment = await EnrollTotpAsync(user.Id);
        await StepUpAsync(current.AccessToken, new { code = enrollment.CodeAt(Factory.Time.GetUtcNow()) });
        var stampBefore = (await FindUserAsync(user.Id)).SecurityStamp;

        using (var regenerate = await Client.PostAsync(RegenerateRoute, content: null, Ct))
        {
            regenerate.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // The other session ended now; this one took the new stamp and keeps refreshing.
        (await FindUserAsync(user.Id)).SecurityStamp.ShouldNotBe(stampBefore);
        using (var otherRefresh = await RefreshAsync(other.RefreshToken))
        {
            await AssertProblemAsync(otherRefresh, HttpStatusCode.Unauthorized, "auth.invalid_refresh_token");
        }

        using var currentRefresh = await RefreshAsync(current.RefreshToken);
        currentRefresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await currentRefresh.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("sessionId").GetGuid().ShouldBe(current.SessionId);

        var sessions = await QueryAsync(context => context.Set<UserSession>().AsNoTracking().ToListAsync(Ct));
        sessions.Single(session => session.Id == other.SessionId).RevokedReason.ShouldBe(SessionRevokedReason.MfaChanged);
        sessions.Single(session => session.Id == current.SessionId).RevokedAt.ShouldBeNull();
        var audit = await QueryAsync(context => context.Set<AuthAuditLog>().SingleAsync(entry => entry.EventType == AuthAuditEvents.RecoveryCodesRegenerated, Ct));
        audit.SessionId.ShouldBe(current.SessionId);
        audit.Details.ShouldBe("""{"revokedSessionCount":1}""");
    }

    [Fact]
    public async Task Regenerate_without_mfa_is_409_and_with_a_password_step_up_is_403()
    {
        // No confirmed method: nothing to regenerate, whatever the token proves.
        await SignInAsync();
        using (var notEnabled = await Client.PostAsync(RegenerateRoute, content: null, Ct))
        {
            await AssertProblemAsync(notEnabled, HttpStatusCode.Conflict, "auth.mfa_not_enabled");
        }

        // With a method but only a password proved: a step-up with a code is required.
        var signedIn = await SignInAsync();
        await EnrollTotpAsync(signedIn.UserId);
        using var refused = await Client.PostAsync(RegenerateRoute, content: null, Ct);
        await AssertProblemAsync(refused, HttpStatusCode.Forbidden, "auth.step_up_required");
        (await QueryAsync(context => context.Set<RecoveryCode>().CountAsync(Ct))).ShouldBe(RecoveryCodeFormat.Count);
    }

    [Fact]
    public async Task Regenerated_codes_are_shown_once_and_never_stored_or_logged()
    {
        var logs = new RecordingLoggerProvider();
        await using var host = LoggingHost(logs);
        var signedIn = await SignInAsync();
        var enrollment = await EnrollTotpAsync(signedIn.UserId);
        using var client = await CreateClientAsync(host);
        using (var stepUp = await client.PostAsJsonAsync(StepUpRoute, new { code = enrollment.CodeAt(Factory.Time.GetUtcNow()) }, Ct))
        {
            stepUp.StatusCode.ShouldBe(HttpStatusCode.OK);
            AuthTestHarness.Authorize(client, (await stepUp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString());
        }

        using var regenerate = await client.PostAsync(RegenerateRoute, content: null, Ct);

        regenerate.StatusCode.ShouldBe(HttpStatusCode.OK);
        var shown = (await regenerate.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("recoveryCodes")
            .EnumerateArray().Select(item => item.GetString()!).ToList();
        var normalized = shown.Select(code => RecoveryCodeFormat.Normalize(code)!).ToList();

        // Hashes only: no text column of any table holds a code in either spelling, and the audit row and the outbox do not.
        (await DatabaseTextScanner.ColumnsContainingAsync(Factory.Services, [.. shown, .. normalized], Ct)).ShouldBeEmpty();
        (await DatabaseTextScanner.ColumnsContainingAsync(Factory.Services, [signedIn.Email], Ct)).ShouldContain("auth.Users.Email");

        // Shown once: the method list never repeats them, and every log line of every level lacks them.
        using var methods = await client.GetAsync(MethodsRoute, Ct);
        var methodsText = await methods.Content.ReadAsStringAsync(Ct);
        normalized.ShouldAllBe(code => !methodsText.Contains(code, StringComparison.OrdinalIgnoreCase));
        logs.Entries.ShouldContain(entry => entry.Contains("Completed RegenerateRecoveryCodesCommand", StringComparison.Ordinal));
        logs.Entries.ShouldAllBe(entry => !shown.Any(code => entry.Contains(code, StringComparison.OrdinalIgnoreCase))
            && !normalized.Any(code => entry.Contains(code, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Change_password_of_an_mfa_account_needs_a_fresh_otp_step_up()
    {
        var user = await CreateUserAsync(Email);
        var login = await LoginAsync();
        var enrollment = await EnrollTotpAsync(user.Id);
        var hashBefore = (await FindUserAsync(user.Id)).PasswordHash;
        AuthTestHarness.Authorize(Client, login.AccessToken);

        // The current password in the body is not enough for an MFA account: 403, audited, nothing counted, nothing changed.
        using (var refused = await ChangePasswordAsync())
        {
            await AssertProblemAsync(refused, HttpStatusCode.Forbidden, "auth.step_up_required");
        }

        var stored = await FindUserAsync(user.Id);
        stored.PasswordHash.ShouldBe(hashBefore);
        stored.AccessFailedCount.ShouldBe(0);
        var failure = await QueryAsync(context => context.Set<AuthAuditLog>().SingleAsync(entry => entry.EventType == AuthAuditEvents.PasswordChangeFailed, Ct));
        failure.Succeeded.ShouldBeFalse();
        failure.FailureReason.ShouldBe("auth.step_up_required");
        failure.UserId.ShouldBe(user.Id);

        // After a step-up with a code the same request succeeds.
        await StepUpAsync(login.AccessToken, new { code = enrollment.CodeAt(Factory.Time.GetUtcNow()) });
        using (var changed = await ChangePasswordAsync())
        {
            changed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await FindUserAsync(user.Id)).PasswordHash.ShouldNotBe(hashBefore);
        (await QueryAsync(context => context.Set<AuthAuditLog>().CountAsync(entry => entry.EventType == AuthAuditEvents.PasswordChanged, Ct))).ShouldBe(1);
    }

    [Fact]
    public async Task Change_password_of_a_password_only_account_needs_no_step_up()
    {
        // A pending setup protects nothing: the account is still password-only, so the current password in the body is the proof.
        var user = await CreateUserAsync(Email);
        var login = await LoginAsync();
        AuthTestHarness.Authorize(Client, login.AccessToken);
        using (var setup = await Client.PostAsJsonAsync(SetupRoute, new { }, Ct))
        {
            setup.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        Factory.Time.Advance(TimeSpan.FromMinutes(30));
        var renewed = await RenewAccessTokenFromRefreshAsync(login.RefreshToken);
        AuthTestHarness.Authorize(Client, renewed);

        using var response = await ChangePasswordAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await FindUserAsync(user.Id)).PasswordHistory.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Every_route_requires_a_token()
    {
        using var disable = await Client.DeleteAsync(MethodRoute(Guid.NewGuid()), Ct);
        using var regenerate = await Client.PostAsync(RegenerateRoute, content: null, Ct);

        foreach (var response in new[] { disable, regenerate })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("http.401");
        }
    }

    [GeneratedRegex("\"traceId\":\"[0-9a-f]+\"")]
    private static partial Regex TraceId();

    [GeneratedRegex("^[2-9A-HJ-NP-Z]{4}(-[2-9A-HJ-NP-Z]{4}){3}$")]
    private static partial Regex DisplayedRecoveryCode();

    private static string MethodRoute(Guid methodId) => $"{MethodsRoute}/{methodId}";

    private static List<string> Amr(JsonWebToken token) => [.. token.Claims.Where(claim => claim.Type == "amr").Select(claim => claim.Value)];

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe(code);
    }

    /// <summary>A host whose only logger is <paramref name="logs"/>, which records every entry of every level and category.</summary>
    private WebApplicationFactory<Program> LoggingHost(RecordingLoggerProvider logs)
        => Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILoggerFactory>();
            services.AddSingleton<ILoggerFactory>(_ => new LoggerFactory([logs]));
        }));

    /// <summary>
    /// Steps up the session of <paramref name="accessToken"/> with <paramref name="proof"/> (a code or a recovery code), makes
    /// <see cref="IntegrationTestBase.Client"/> send the new access token and returns it.
    /// </summary>
    private async Task<JsonWebToken> StepUpAsync(string accessToken, object proof)
    {
        AuthTestHarness.Authorize(Client, accessToken);
        using var response = await Client.PostAsJsonAsync(StepUpRoute, proof, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()!;
        AuthTestHarness.Authorize(Client, token);
        return new JsonWebToken(token);
    }

    private Task<HttpResponseMessage> ChangePasswordAsync()
        => Client.PostAsJsonAsync(ChangePasswordRoute, new { currentPassword = AuthTestHarness.Password, newPassword = NewPassword }, Ct);

    private async Task<JsonElement> GetMethodsAsync()
    {
        using var response = await Client.GetAsync(MethodsRoute, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<Tokens> LoginAsync()
    {
        using var response = await Client.PostAsJsonAsync(LoginRoute, new { email = Email, password = AuthTestHarness.Password }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return new Tokens(body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!, body.GetProperty("sessionId").GetGuid());
    }

    private async Task<string> RenewAccessTokenFromRefreshAsync(string refreshToken)
    {
        using var response = await RefreshAsync(refreshToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()!;
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken)
        => Client.PostAsJsonAsync(RefreshRoute, new { refreshToken }, Ct);

    private Task<User> FindUserAsync(Guid userId)
        => QueryAsync(context => context.Set<User>().AsNoTracking().Include(user => user.PasswordHistory).SingleAsync(user => user.Id == userId, Ct));

    private async Task<T> QueryAsync<T>(Func<AuthDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    private sealed record Tokens(string AccessToken, string RefreshToken, Guid SessionId);
}
