using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// TOTP enrollment through the running host: <c>POST /api/v1/auth/mfa/totp/setup</c>, <c>POST /api/v1/auth/mfa/totp/confirm</c> and
/// <c>GET /api/v1/auth/mfa/methods</c>. The secret and the recovery codes are shown once and found nowhere else (later answers, any
/// table, any log line); a confirm ends the other sessions and keeps this one; both writes need a fresh step-up.
/// </summary>
public sealed partial class MfaEnrollmentTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string SetupRoute = "/api/v1/auth/mfa/totp/setup";
    private const string ConfirmRoute = "/api/v1/auth/mfa/totp/confirm";
    private const string MethodsRoute = "/api/v1/auth/mfa/methods";
    private const string StepUpRoute = "/api/v1/auth/step-up";
    private const string LoginRoute = "/api/v1/auth/login";
    private const string RefreshRoute = "/api/v1/auth/token/refresh";
    private const string Email = "alice@example.com";

    [Fact]
    public async Task Setup_returns_a_secret_and_otpauth_uri_and_confirm_enables_mfa()
    {
        var signedIn = await SignInAsync();

        // Nothing to confirm before a setup.
        using (var early = await Client.PostAsJsonAsync(ConfirmRoute, new { code = "123456" }, Ct))
        {
            await AssertProblemAsync(early, HttpStatusCode.Conflict, "auth.mfa_setup_not_pending");
        }

        using var setup = await Client.PostAsJsonAsync(SetupRoute, new { label = "Phone" }, Ct);

        setup.StatusCode.ShouldBe(HttpStatusCode.OK);
        var setupBody = await setup.Content.ReadFromJsonAsync<JsonElement>(Ct);
        setupBody.EnumerateObject().Select(property => property.Name).ShouldBe(["methodId", "secret", "otpAuthUri"]);
        var secret = Base32.Decode(setupBody.GetProperty("secret").GetString()!);
        secret.Length.ShouldBe(Totp.SecretLength);
        var issuer = Factory.Services.GetRequiredService<IOptions<MfaOptions>>().Value.Issuer;
        setupBody.GetProperty("otpAuthUri").GetString().ShouldBe(TotpUri.Build(issuer, signedIn.Email, secret));
        var methodId = setupBody.GetProperty("methodId").GetGuid();
        var stored = await QueryAsync(context => context.Set<UserMfaMethod>().AsNoTracking().SingleAsync(Ct));
        stored.Id.ShouldBe(methodId);
        stored.Label.ShouldBe("Phone");
        stored.IsConfirmed.ShouldBeFalse();

        // A pending setup protects nothing yet.
        var pending = await GetMethodsAsync();
        pending.GetProperty("enabled").GetBoolean().ShouldBeFalse();
        pending.GetProperty("recoveryCodesRemaining").GetInt32().ShouldBe(0);
        pending.GetProperty("methods").EnumerateArray().ShouldHaveSingleItem().GetProperty("confirmed").GetBoolean().ShouldBeFalse();

        Factory.Time.Advance(TimeSpan.FromSeconds(20));
        var confirmedAt = Factory.Time.GetUtcNow();
        var code = Totp.ComputeCode(secret, Totp.TimeStepAt(confirmedAt));

        using var confirm = await Client.PostAsJsonAsync(ConfirmRoute, new { code }, Ct);

        confirm.StatusCode.ShouldBe(HttpStatusCode.OK);
        var confirmBody = await confirm.Content.ReadFromJsonAsync<JsonElement>(Ct);
        confirmBody.EnumerateObject().Select(property => property.Name).ShouldBe(["recoveryCodes"]);
        var recoveryCodes = confirmBody.GetProperty("recoveryCodes").EnumerateArray().Select(item => item.GetString()!).ToList();
        recoveryCodes.Count.ShouldBe(RecoveryCodeFormat.Count);
        recoveryCodes.ShouldAllBe(recoveryCode => DisplayedRecoveryCode().IsMatch(recoveryCode));

        var methods = await GetMethodsAsync();
        methods.EnumerateObject().Select(property => property.Name).ShouldBe(["enabled", "recoveryCodesRemaining", "methods"]);
        methods.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        methods.GetProperty("recoveryCodesRemaining").GetInt32().ShouldBe(10);
        var method = methods.GetProperty("methods").EnumerateArray().ShouldHaveSingleItem();
        method.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["id", "type", "label", "confirmed", "createdAt", "confirmedAt", "lastUsedAt"]);
        method.GetProperty("id").GetGuid().ShouldBe(methodId);
        method.GetProperty("type").GetString().ShouldBe("Totp");
        method.GetProperty("label").GetString().ShouldBe("Phone");
        method.GetProperty("confirmed").GetBoolean().ShouldBeTrue();
        method.GetProperty("createdAt").GetDateTimeOffset().ShouldBe(confirmedAt.AddSeconds(-20));
        method.GetProperty("confirmedAt").GetDateTimeOffset().ShouldBe(confirmedAt);
        method.GetProperty("lastUsedAt").GetDateTimeOffset().ShouldBe(confirmedAt);

        // One authenticator per user: a second setup is refused while this one is on (replacing it is remove, then set up).
        using (var again = await Client.PostAsJsonAsync(SetupRoute, new { }, Ct))
        {
            await AssertProblemAsync(again, HttpStatusCode.Conflict, "auth.mfa_already_enabled");
        }

        // The confirm spent the code's time step, so the same code cannot step up (or sign in) afterwards.
        Factory.Time.Advance(TimeSpan.FromSeconds(5));
        using var replay = await Client.PostAsJsonAsync(StepUpRoute, new { code }, Ct);
        await AssertProblemAsync(replay, HttpStatusCode.BadRequest, "auth.invalid_mfa_code");
    }

    [Fact]
    public async Task Setup_secret_appears_only_in_the_setup_response()
    {
        var logs = new RecordingLoggerProvider();
        await using var host = LoggingHost(logs);
        var signedIn = await SignInAsync();
        using var client = await CreateClientAsync(host);

        using var setup = await client.PostAsync(SetupRoute, content: null, Ct);

        setup.StatusCode.ShouldBe(HttpStatusCode.OK);
        var setupBody = await setup.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var secretText = setupBody.GetProperty("secret").GetString()!;
        var uri = setupBody.GetProperty("otpAuthUri").GetString()!;
        var secret = Base32.Decode(secretText);

        // Every later answer: the confirm, the method list and a refused second setup.
        using var confirm = await client.PostAsJsonAsync(ConfirmRoute, new { code = CurrentCode(secret) }, Ct);
        using var methods = await client.GetAsync(MethodsRoute, Ct);
        using var again = await client.PostAsJsonAsync(SetupRoute, new { }, Ct);
        confirm.StatusCode.ShouldBe(HttpStatusCode.OK);
        methods.StatusCode.ShouldBe(HttpStatusCode.OK);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        foreach (var response in new[] { confirm, methods, again })
        {
            (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain(secretText, Case.Insensitive);
        }

        // The audit rows and the outbox exist, and neither they nor any other text column of any table holds the secret.
        var audits = await QueryAsync(context => context.Set<AuthAuditLog>().AsNoTracking().OrderBy(entry => entry.Id).ToListAsync(Ct));
        audits.Select(entry => entry.EventType).ShouldBe([AuthAuditEvents.MfaSetupStarted, AuthAuditEvents.MfaEnabled]);
        audits[0].Details.ShouldBe($$"""{"methodId":"{{setupBody.GetProperty("methodId").GetGuid()}}"}""");
        var outbox = await QueryAsync(context => context.Set<OutboxMessage>().AsNoTracking().ToListAsync(Ct));
        outbox.ShouldContain(message => message.Type.Contains(nameof(MfaChangedDomainEvent), StringComparison.Ordinal));
        var secretForms = new[] { secretText, Convert.ToHexString(secret), Convert.ToBase64String(secret).TrimEnd('=') };
        (await TextColumnsContainingAsync(secretForms)).ShouldBeEmpty();
        (await TextColumnsContainingAsync([signedIn.Email])).ShouldContain("auth.Users.Email");

        // Every log line of every level, from every category (the handlers' own lines included), lacks the secret in each form and the URI.
        logs.Entries.ShouldContain(entry => entry.Contains("Completed SetUpTotpCommand", StringComparison.Ordinal));
        logs.Entries.ShouldAllBe(entry => !secretForms.Any(form => entry.Contains(form, StringComparison.OrdinalIgnoreCase))
            && !entry.Contains(uri, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Recovery_codes_are_shown_once_and_never_stored_or_logged()
    {
        var logs = new RecordingLoggerProvider();
        await using var host = LoggingHost(logs);
        var signedIn = await SignInAsync();
        using var client = await CreateClientAsync(host);
        var secret = await SetUpAsync(client);

        using var confirm = await client.PostAsJsonAsync(ConfirmRoute, new { code = CurrentCode(secret) }, Ct);

        confirm.StatusCode.ShouldBe(HttpStatusCode.OK);
        var shown = (await confirm.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("recoveryCodes")
            .EnumerateArray().Select(item => item.GetString()!).ToList();
        shown.Count.ShouldBe(RecoveryCodeFormat.Count);
        var normalized = shown.Select(code => RecoveryCodeFormat.Normalize(code)!).ToList();

        // Stored as hashes only: one row per shown code, and no text column of any table holds a code in either spelling.
        var stored = await QueryAsync(context => context.Set<RecoveryCode>().AsNoTracking().ToListAsync(Ct));
        stored.Select(code => Convert.ToHexString(code.CodeHash))
            .ShouldBe(normalized.Select(code => Convert.ToHexString(RecoveryCodeFormat.Hash(code))), ignoreOrder: true);
        stored.ShouldAllBe(code => code.UserId == signedIn.UserId && code.UsedAt == null);
        (await TextColumnsContainingAsync([.. shown, .. normalized])).ShouldBeEmpty();
        (await TextColumnsContainingAsync([signedIn.Email])).ShouldContain("auth.Users.Email");

        // Shown once: nothing later shows them again.
        using var methods = await client.GetAsync(MethodsRoute, Ct);
        var methodsText = await methods.Content.ReadAsStringAsync(Ct);
        using var again = await client.PostAsJsonAsync(ConfirmRoute, new { code = CurrentCode(secret) }, Ct);
        await AssertProblemAsync(again, HttpStatusCode.Conflict, "auth.mfa_setup_not_pending");
        normalized.ShouldAllBe(code => !methodsText.Contains(code, StringComparison.OrdinalIgnoreCase));

        logs.Entries.ShouldContain(entry => entry.Contains("Completed ConfirmTotpCommand", StringComparison.Ordinal));
        logs.Entries.ShouldAllBe(entry => !shown.Any(code => entry.Contains(code, StringComparison.OrdinalIgnoreCase))
            && !normalized.Any(code => entry.Contains(code, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Confirm_ends_other_sessions_and_keeps_this_one()
    {
        await CreateUserAsync(Email);
        var current = await LoginAsync();
        var other = await LoginAsync();
        AuthTestHarness.Authorize(Client, current.AccessToken);
        var secret = await SetUpAsync(Client);

        using (var confirm = await Client.PostAsJsonAsync(ConfirmRoute, new { code = CurrentCode(secret) }, Ct))
        {
            confirm.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // The other session ended now; this one took the new security stamp and keeps refreshing.
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
        var enabled = await QueryAsync(context => context.Set<AuthAuditLog>().AsNoTracking()
            .SingleAsync(entry => entry.EventType == AuthAuditEvents.MfaEnabled, Ct));
        enabled.SessionId.ShouldBe(current.SessionId);
        enabled.Details.ShouldBe("""{"revokedSessionCount":1}""");
    }

    [Fact]
    public async Task Setup_without_fresh_step_up_is_403_step_up_required()
    {
        await CreateUserAsync(Email);
        var login = await LoginAsync();
        var signedInAt = Factory.Time.GetUtcNow();
        Factory.Time.Advance(TimeSpan.FromMinutes(11));

        // A refresh renews the access token but not the proof: auth_time stays the sign-in's.
        using var refresh = await RefreshAsync(login.RefreshToken);
        refresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        var refreshed = (await refresh.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()!;
        new JsonWebToken(refreshed).GetClaim("auth_time").Value.ShouldBe(signedInAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        AuthTestHarness.Authorize(Client, refreshed);

        using (var stale = await Client.PostAsJsonAsync(SetupRoute, new { }, Ct))
        {
            await AssertProblemAsync(stale, HttpStatusCode.Forbidden, "auth.step_up_required");
        }

        (await QueryAsync(context => context.Set<UserMfaMethod>().CountAsync(Ct))).ShouldBe(0);

        using var stepUp = await Client.PostAsJsonAsync(StepUpRoute, new { password = AuthTestHarness.Password }, Ct);
        stepUp.StatusCode.ShouldBe(HttpStatusCode.OK);
        AuthTestHarness.Authorize(Client, (await stepUp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString());

        var secret = await SetUpAsync(Client);

        // The confirm needs a fresh step-up too: ten minutes later it is refused, and the setup stays pending.
        Factory.Time.Advance(TimeSpan.FromMinutes(10));
        using var lateConfirm = await Client.PostAsJsonAsync(ConfirmRoute, new { code = CurrentCode(secret) }, Ct);
        await AssertProblemAsync(lateConfirm, HttpStatusCode.Forbidden, "auth.step_up_required");
        (await QueryAsync(context => context.Set<UserMfaMethod>().AsNoTracking().SingleAsync(Ct))).IsConfirmed.ShouldBeFalse();
    }

    [Fact]
    public async Task Responses_are_no_store()
    {
        await SignInAsync();

        using var setup = await Client.PostAsJsonAsync(SetupRoute, new { label = "Phone" }, Ct);
        setup.StatusCode.ShouldBe(HttpStatusCode.OK);
        var secret = Base32.Decode((await setup.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("secret").GetString()!);
        var code = CurrentCode(secret);
        using var wrong = await Client.PostAsJsonAsync(ConfirmRoute, new { code = code == "000000" ? "111111" : "000000" }, Ct);
        using var confirm = await Client.PostAsJsonAsync(ConfirmRoute, new { code }, Ct);
        using var conflict = await Client.PostAsJsonAsync(SetupRoute, new { }, Ct);

        // The wrong code is a 400 the client can show, and it is not counted toward the lockout: the caller made this secret.
        await AssertProblemAsync(wrong, HttpStatusCode.BadRequest, "auth.invalid_mfa_code");
        confirm.StatusCode.ShouldBe(HttpStatusCode.OK);
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        foreach (var response in new[] { setup, wrong, confirm, conflict })
        {
            response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
            response.Headers.Pragma.ToString().ShouldBe("no-cache");
        }

        (await QueryAsync(context => context.Set<User>().AsNoTracking().SingleAsync(Ct))).AccessFailedCount.ShouldBe(0);
    }

    [Fact]
    public async Task Every_route_requires_a_token()
    {
        using var setup = await Client.PostAsJsonAsync(SetupRoute, new { }, Ct);
        using var confirm = await Client.PostAsJsonAsync(ConfirmRoute, new { code = "123456" }, Ct);
        using var methods = await Client.GetAsync(MethodsRoute, Ct);

        foreach (var response in new[] { setup, confirm, methods })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("http.401");
        }
    }

    [Fact]
    public async Task Label_and_code_are_validated_and_the_label_is_optional()
    {
        await SignInAsync();

        using (var blank = await Client.PostAsJsonAsync(SetupRoute, new { label = "   " }, Ct))
        {
            await AssertProblemAsync(blank, HttpStatusCode.BadRequest, "validation.failed");
        }

        using (var tooLong = await Client.PostAsJsonAsync(SetupRoute, new { label = new string('l', UserMfaMethod.MaxLabelLength + 1) }, Ct))
        {
            await AssertProblemAsync(tooLong, HttpStatusCode.BadRequest, "validation.failed");
        }

        // No body at all: the default label.
        using (var noBody = await Client.PostAsync(SetupRoute, content: null, Ct))
        {
            noBody.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await QueryAsync(context => context.Set<UserMfaMethod>().AsNoTracking().SingleAsync(Ct))).Label.ShouldBe("Authenticator app");

        foreach (var code in new[] { "12345", "1234567", "12345a", string.Empty })
        {
            using var invalid = await Client.PostAsJsonAsync(ConfirmRoute, new { code }, Ct);
            await AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "validation.failed");
        }
    }

    [Fact]
    public async Task Simultaneous_setups_store_one_method_whose_secret_one_of_them_shows()
    {
        await SignInAsync();

        var responses = await Task.WhenAll(
            Client.PostAsJsonAsync(SetupRoute, new { label = "First" }, Ct),
            Client.PostAsJsonAsync(SetupRoute, new { label = "Second" }, Ct));

        var secrets = new List<string>();
        foreach (var response in responses)
        {
            using (response)
            {
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                secrets.Add((await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("secret").GetString()!);
            }
        }

        var method = await QueryAsync(context => context.Set<UserMfaMethod>().AsNoTracking().SingleAsync(Ct));
        var stored = Base32.Encode(Factory.Services.GetRequiredService<IMfaSecretProtector>().Unprotect(method.SecretEncrypted));
        secrets.ShouldContain(stored);
    }

    [Fact]
    public async Task Simultaneous_confirms_enable_once_and_never_fail_with_a_500()
    {
        // The first confirm is held just before its save; the second runs through and commits; then the first saves.
        var gate = new MethodSaveGate(entry => entry.Property(method => method.ConfirmedAt).IsModified);
        await using var host = GatedHost(gate);
        await SignInAsync();
        using var client = await CreateClientAsync(host);
        var code = CurrentCode(await SetUpAsync(client));

        var first = client.PostAsJsonAsync(ConfirmRoute, new { code }, Ct);
        await gate.Entered;
        using (var second = await client.PostAsJsonAsync(ConfirmRoute, new { code }, Ct))
        {
            second.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        gate.Release();

        // The user's RowVersion (and the method's tokens) refuse the held save: one set of codes, never a 500.
        using var held = await first;
        await AssertProblemAsync(held, HttpStatusCode.Conflict, "concurrency.conflict");
        (await QueryAsync(context => context.Set<RecoveryCode>().CountAsync(Ct))).ShouldBe(RecoveryCodeFormat.Count);
        (await QueryAsync(context => context.Set<AuthAuditLog>().CountAsync(entry => entry.EventType == AuthAuditEvents.MfaEnabled, Ct))).ShouldBe(1);
    }

    [Fact]
    public async Task Confirm_racing_a_setup_restart_that_commits_first_is_a_conflict()
    {
        // The confirm verified its code against secret A and is held before its save; a setup restart commits secret B meanwhile.
        var gate = new MethodSaveGate(entry => entry.Property(method => method.ConfirmedAt).IsModified);
        await using var host = GatedHost(gate);
        await SignInAsync();
        using var client = await CreateClientAsync(host);
        var code = CurrentCode(await SetUpAsync(client));

        var confirm = client.PostAsJsonAsync(ConfirmRoute, new { code }, Ct);
        await gate.Entered;
        var restartedSecret = Base32.Encode(await SetUpAsync(client));
        gate.Release();

        // No secret nobody verified is ever confirmed: the confirm loses, and the restarted setup stands, still pending.
        using var held = await confirm;
        await AssertProblemAsync(held, HttpStatusCode.Conflict, "concurrency.conflict");
        var method = await QueryAsync(context => context.Set<UserMfaMethod>().AsNoTracking().SingleAsync(Ct));
        method.IsConfirmed.ShouldBeFalse();
        Base32.Encode(Factory.Services.GetRequiredService<IMfaSecretProtector>().Unprotect(method.SecretEncrypted)).ShouldBe(restartedSecret);
        (await QueryAsync(context => context.Set<RecoveryCode>().CountAsync(Ct))).ShouldBe(0);
        (await QueryAsync(context => context.Set<AuthAuditLog>().CountAsync(entry => entry.EventType == AuthAuditEvents.MfaEnabled, Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Setup_restart_racing_a_confirm_that_commits_first_is_a_conflict()
    {
        // A second setup loaded the method while it was pending and is held before its save; the confirm commits meanwhile.
        var gate = new MethodSaveGate(entry => entry.Property(method => method.SecretEncrypted).IsModified);
        await using var host = GatedHost(gate);
        await SignInAsync();
        using var client = await CreateClientAsync(host);
        var secret = await SetUpAsync(client);

        var restart = client.PostAsJsonAsync(SetupRoute, new { label = "Other phone" }, Ct);
        await gate.Entered;
        using (var confirm = await client.PostAsJsonAsync(ConfirmRoute, new { code = CurrentCode(secret) }, Ct))
        {
            confirm.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        gate.Release();

        // The setup cannot replace the secret of the method that is now on (which would bypass the MFA step-up rule): it loses.
        using var held = await restart;
        await AssertProblemAsync(held, HttpStatusCode.Conflict, "concurrency.conflict");
        var method = await QueryAsync(context => context.Set<UserMfaMethod>().AsNoTracking().SingleAsync(Ct));
        method.IsConfirmed.ShouldBeTrue();
        method.Label.ShouldBe("Authenticator app");
        Factory.Services.GetRequiredService<IMfaSecretProtector>().Unprotect(method.SecretEncrypted).ShouldBe(secret);
        (await QueryAsync(context => context.Set<AuthAuditLog>().CountAsync(entry => entry.EventType == AuthAuditEvents.MfaSetupStarted, Ct))).ShouldBe(1);
    }

    [GeneratedRegex("^[2-9A-HJ-NP-Z]{4}(-[2-9A-HJ-NP-Z]{4}){3}$")]
    private static partial Regex DisplayedRecoveryCode();

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

    /// <summary>A host whose Auth context runs <paramref name="gate"/> on every save, so a test can interleave two requests exactly.</summary>
    private WebApplicationFactory<Program> GatedHost(MethodSaveGate gate)
        => Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<AuthDbContext>(options => options.AddInterceptors(gate))));

    private string CurrentCode(byte[] secret) => Totp.ComputeCode(secret, Totp.TimeStepAt(Factory.Time.GetUtcNow()));

    /// <summary>Starts a setup through <paramref name="client"/> and returns the plain secret it showed.</summary>
    private async Task<byte[]> SetUpAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(SetupRoute, new { }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return Base32.Decode((await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("secret").GetString()!);
    }

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

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken)
        => Client.PostAsJsonAsync(RefreshRoute, new { refreshToken }, Ct);

    /// <summary>The <c>schema.table.column</c> of every text column of the test database that holds one of <paramref name="values"/>.</summary>
    private Task<List<string>> TextColumnsContainingAsync(IReadOnlyList<string> values)
        => DatabaseTextScanner.ColumnsContainingAsync(Factory.Services, values, Ct);

    private async Task<T> QueryAsync<T>(Func<AuthDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    private sealed record Tokens(string AccessToken, string RefreshToken, Guid SessionId);

    /// <summary>
    /// Holds the first save that updates a loaded <see cref="UserMfaMethod"/> matching <paramref name="holds"/>, after the request has
    /// read and changed everything and before any SQL runs, until <see cref="Release"/>; every other save passes. No sleeps: the test
    /// awaits <see cref="Entered"/>, runs the competing request to completion, then releases.
    /// </summary>
    private sealed class MethodSaveGate(Func<EntityEntry<UserMfaMethod>, bool> holds) : SaveChangesInterceptor
    {
        private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _held;

        public Task Entered => _entered.Task.WaitAsync(Limit);

        public void Release() => _released.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } context)
            {
                context.ChangeTracker.DetectChanges();
                var matches = context.ChangeTracker.Entries<UserMfaMethod>().Any(entry => entry.State == EntityState.Modified && holds(entry));
                if (matches && Interlocked.Exchange(ref _held, 1) == 0)
                {
                    _entered.TrySetResult();
                    await _released.Task.WaitAsync(Limit, cancellationToken);
                }
            }

            return result;
        }
    }
}
