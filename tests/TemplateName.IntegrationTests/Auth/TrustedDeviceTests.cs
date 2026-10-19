using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.TrustedDevices;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// Trusted devices through the running host: <c>rememberDevice</c> at <c>POST /api/v1/auth/mfa/verify</c> returns a device token once,
/// <c>trustedDeviceToken</c> at <c>POST /api/v1/auth/login</c> skips the challenge while the device is usable (30 days absolute, not
/// revoked, the user's security stamp unchanged), and <c>GET</c> and <c>DELETE /api/v1/auth/trusted-devices</c> list and forget the
/// caller's own devices. A remembered device stands in for the code only: its sessions carry <c>amr</c> <c>pwd</c> and never satisfy an
/// MFA step-up. Every anonymous request comes from its own client address, so the strict limit never decides an outcome by accident.
/// </summary>
public sealed class TrustedDeviceTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string LoginRoute = "/api/v1/auth/login";
    private const string VerifyRoute = "/api/v1/auth/mfa/verify";
    private const string DevicesRoute = "/api/v1/auth/trusted-devices";
    private const string MethodsRoute = "/api/v1/auth/mfa/methods";
    private const string RegenerateRoute = "/api/v1/auth/mfa/recovery-codes/regenerate";
    private const string ChangePasswordRoute = "/api/v1/auth/password/change";
    private const string ForgotRoute = "/api/v1/auth/password/forgot";
    private const string ResetRoute = "/api/v1/auth/password/reset";
    private const string AdminUsersRoute = "/api/v1/admin/auth/users";
    private const string Email = "alice@example.com";
    private const string OtherEmail = "bob@example.com";
    private const string NewPassword = "a brand new passphrase";
    private const string UserAgent = "TrustedDeviceTests/1.0";

    private static readonly TimeSpan TrustedDeviceLifetime = TimeSpan.FromDays(30);

    private int _addressCount;

    [Fact]
    public async Task Remembered_device_signs_in_without_a_code_for_thirty_days()
    {
        var user = await CreateUserAsync(Email);
        var enrollment = await EnrollTotpAsync(user.Id);
        var rememberedAt = Factory.Time.GetUtcNow();

        var remembered = await RememberAsync(Email, enrollment, deviceName: "Alice's laptop");

        // The token is shown once with its absolute expiry; only its SHA-256 is stored, bound to the user and the current stamp.
        remembered.DeviceToken.Length.ShouldBe(43);
        remembered.ExpiresAt.ShouldBe(rememberedAt + TrustedDeviceLifetime);
        var device = await QueryAsync(context => context.Set<TrustedDevice>().AsNoTracking().SingleAsync(Ct));
        device.TokenHash.ShouldBe(SHA256.HashData(Encoding.UTF8.GetBytes(remembered.DeviceToken)));
        device.UserId.ShouldBe(user.Id);
        device.DeviceName.ShouldBe("Alice's laptop");
        device.UserAgent.ShouldBe(UserAgent);
        device.SecurityStamp.ShouldBe(user.SecurityStamp);
        device.ExpiresAt.ShouldBe(rememberedAt + TrustedDeviceLifetime);
        (await AuditEventsAsync()).ShouldBe(
            [AuthAuditEvents.MfaChallengeIssued, AuthAuditEvents.MfaVerified, AuthAuditEvents.LoginSucceeded, AuthAuditEvents.TrustedDeviceAdded]);

        // One second before the expiry: the password alone signs in, with amr pwd, and the device records the use.
        Factory.Time.Advance(TrustedDeviceLifetime - TimeSpan.FromSeconds(1));
        var lastMoment = Factory.Time.GetUtcNow();
        using (var login = await LoginAsync(Email, deviceToken: remembered.DeviceToken, deviceName: "Alice's laptop"))
        {
            login.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await login.Content.ReadFromJsonAsync<JsonElement>(Ct);
            body.GetProperty("mfaRequired").GetBoolean().ShouldBeFalse();
            var accessToken = new JsonWebToken(body.GetProperty("accessToken").GetString());
            Amr(accessToken).ShouldBe(["pwd"]);
            var session = await QueryAsync(context => context.Set<UserSession>().AsNoTracking()
                .SingleAsync(candidate => candidate.Id == body.GetProperty("sessionId").GetGuid(), Ct));
            session.AuthMethods.ShouldBe("pwd");
            session.DeviceName.ShouldBe("Alice's laptop");
        }

        var used = await QueryAsync(context => context.Set<TrustedDevice>().AsNoTracking().SingleAsync(Ct));
        used.LastUsedAt.ShouldBe(lastMoment);
        used.ExpiresAt.ShouldBe(rememberedAt + TrustedDeviceLifetime);
        var signedIn = await QueryAsync(context => context.Set<AuthAuditLog>().AsNoTracking().OrderBy(entry => entry.Id).LastAsync(Ct));
        signedIn.EventType.ShouldBe(AuthAuditEvents.LoginSucceeded);
        signedIn.Details.ShouldBe($$"""{"trustedDeviceId":"{{device.Id}}"}""");

        // At the expiry instant the device is over: the challenge again, as without a token.
        Factory.Time.Advance(TimeSpan.FromSeconds(1));
        (await ChallengeOrTokensAsync(Email, remembered.DeviceToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task Device_of_another_user_is_ignored()
    {
        var alice = await CreateUserAsync(Email);
        var aliceEnrollment = await EnrollTotpAsync(alice.Id);
        var bob = await CreateUserAsync(OtherEmail);
        await EnrollTotpAsync(bob.Id);
        var remembered = await RememberAsync(Email, aliceEnrollment);

        // Bob's correct password with Alice's device token: Bob's challenge, exactly as without a token; Alice's device is untouched.
        (await ChallengeOrTokensAsync(OtherEmail, remembered.DeviceToken)).ShouldBeTrue();
        (await QueryAsync(context => context.Set<TrustedDevice>().AsNoTracking().SingleAsync(Ct))).LastUsedAt.ShouldBeNull();

        // And the token still works for its owner.
        (await ChallengeOrTokensAsync(Email, remembered.DeviceToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task Password_change_forgets_every_device()
    {
        var user = await CreateUserAsync(Email);
        var enrollment = await EnrollTotpAsync(user.Id);
        var first = await RememberAsync(Email, enrollment, deviceName: "Laptop");
        Factory.Time.Advance(TimeSpan.FromSeconds(30));
        var second = await RememberAsync(Email, enrollment, deviceName: "Phone");

        // The second verify's session holds a fresh code step-up, which the password change of an MFA account needs.
        using (var change = await SendAsync(HttpMethod.Post, ChangePasswordRoute, second.AccessToken, new { currentPassword = AuthTestHarness.Password, newPassword = NewPassword }))
        {
            change.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // Both devices were bound to the old stamp: the new password gets the challenge with either token.
        (await ChallengeOrTokensAsync(Email, first.DeviceToken, NewPassword)).ShouldBeTrue();
        (await ChallengeOrTokensAsync(Email, second.DeviceToken, NewPassword)).ShouldBeTrue();

        // The rows are still there (cleanup is Plan 5's), but the list leaves them out: they can never be used again.
        (await QueryAsync(context => context.Set<TrustedDevice>().CountAsync(Ct))).ShouldBe(2);
        var page = await ListAsync(second.AccessToken);
        page.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Theory]
    [InlineData("password reset")]
    [InlineData("mfa change")]
    [InlineData("suspension")]
    public async Task Password_reset_mfa_change_and_suspension_forget_every_device(string change)
    {
        var user = await CreateUserAsync(Email);
        var enrollment = await EnrollTotpAsync(user.Id);
        var remembered = await RememberAsync(Email, enrollment);
        var password = AuthTestHarness.Password;

        switch (change)
        {
            case "password reset":
                using (var forgot = await PostAsync(Client, ForgotRoute, new { email = Email }))
                {
                    forgot.StatusCode.ShouldBe(HttpStatusCode.Accepted);
                }

                await DispatchNotificationsAsync();
                using (var reset = await PostAsync(Client, ResetRoute, new { token = Factory.EmailSender.LastLinkToken(Email), newPassword = NewPassword }))
                {
                    reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
                }

                password = NewPassword;
                break;

            case "mfa change":
                // The verify's session holds a fresh code step-up; regenerating the recovery codes rotates the stamp (D13).
                using (var regenerate = await SendAsync(HttpMethod.Post, RegenerateRoute, remembered.AccessToken))
                {
                    regenerate.StatusCode.ShouldBe(HttpStatusCode.OK);
                }

                break;

            default:
                // An administrator locks the account (a new stamp) and unlocks it again (the stamp stays new).
                await SignInAsync("auth.user.lock");
                using (var locked = await Client.PostAsync($"{AdminUsersRoute}/{user.Id}/lock", null, Ct))
                {
                    locked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
                }

                using (var unlocked = await Client.PostAsync($"{AdminUsersRoute}/{user.Id}/unlock", null, Ct))
                {
                    unlocked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
                }

                SignOut();
                break;
        }

        (await FindUserAsync(user.Id)).SecurityStamp.ShouldNotBe(user.SecurityStamp);
        (await ChallengeOrTokensAsync(Email, remembered.DeviceToken, password)).ShouldBeTrue();
        (await QueryAsync(context => context.Set<TrustedDevice>().AsNoTracking().SingleAsync(Ct))).LastUsedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Trusted_device_session_does_not_satisfy_an_mfa_step_up()
    {
        var user = await CreateUserAsync(Email);
        var enrollment = await EnrollTotpAsync(user.Id);
        var remembered = await RememberAsync(Email, enrollment);

        // A sign-in through the device: a brand-new auth_time, but amr pwd, because no code was proved now (D9).
        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        string accessToken;
        using (var login = await LoginAsync(Email, deviceToken: remembered.DeviceToken))
        {
            login.StatusCode.ShouldBe(HttpStatusCode.OK);
            accessToken = (await login.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()!;
        }

        Amr(new JsonWebToken(accessToken)).ShouldBe(["pwd"]);

        // So the actions that need an MFA step-up are refused: turning MFA off, regenerating the codes, changing the password.
        using (var disable = await SendAsync(HttpMethod.Delete, $"{MethodsRoute}/{enrollment.MethodId}", accessToken))
        {
            await AssertProblemAsync(disable, HttpStatusCode.Forbidden, "auth.step_up_required");
        }

        using (var regenerate = await SendAsync(HttpMethod.Post, RegenerateRoute, accessToken))
        {
            await AssertProblemAsync(regenerate, HttpStatusCode.Forbidden, "auth.step_up_required");
        }

        using (var change = await SendAsync(HttpMethod.Post, ChangePasswordRoute, accessToken, new { currentPassword = AuthTestHarness.Password, newPassword = NewPassword }))
        {
            await AssertProblemAsync(change, HttpStatusCode.Forbidden, "auth.step_up_required");
        }

        (await QueryAsync(context => context.Set<TrustedDevice>().CountAsync(device => device.RevokedAt == null, Ct))).ShouldBe(1);
    }

    [Fact]
    public async Task List_shows_only_my_usable_devices_paged_by_cursor()
    {
        var alice = await CreateUserAsync(Email);
        var aliceEnrollment = await EnrollTotpAsync(alice.Id);
        var bob = await CreateUserAsync(OtherEmail);
        var bobEnrollment = await EnrollTotpAsync(bob.Id);
        var oldest = await RememberAsync(Email, aliceEnrollment, deviceName: "Desktop");
        Factory.Time.Advance(TimeSpan.FromSeconds(30));
        var middle = await RememberAsync(Email, aliceEnrollment, deviceName: "Laptop");
        Factory.Time.Advance(TimeSpan.FromSeconds(30));
        var newest = await RememberAsync(Email, aliceEnrollment, deviceName: "Phone");
        var bobs = await RememberAsync(OtherEmail, bobEnrollment, deviceName: "Bob's phone");

        // Newest first by default, two per page, the total on request; Bob's device never appears.
        var first = await ListAsync(newest.AccessToken, "?pageSize=2&includeTotalCount=true");
        DeviceNames(first).ShouldBe(["Phone", "Laptop"]);
        first.GetProperty("totalCount").GetInt64().ShouldBe(3);
        var item = first.GetProperty("items")[0];
        item.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["id", "deviceName", "userAgent", "createdAt", "expiresAt", "lastUsedAt"], ignoreOrder: true);
        item.GetProperty("userAgent").GetString().ShouldBe(UserAgent);
        item.GetProperty("expiresAt").GetDateTimeOffset().ShouldBe(newest.ExpiresAt);
        item.GetProperty("lastUsedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        var cursor = first.GetProperty("nextCursor").GetString().ShouldNotBeNull();

        var second = await ListAsync(newest.AccessToken, $"?pageSize=2&cursor={Uri.EscapeDataString(cursor)}");
        DeviceNames(second).ShouldBe(["Desktop"]);
        second.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);

        // Oldest first on request; a field outside the allow-list is refused.
        DeviceNames(await ListAsync(oldest.AccessToken, "?sort=createdAt")).ShouldBe(["Desktop", "Laptop", "Phone"]);
        using (var unsorted = await SendAsync(HttpMethod.Get, $"{DevicesRoute}?sort=lastUsedAt", middle.AccessToken))
        {
            await AssertProblemAsync(unsorted, HttpStatusCode.BadRequest, "pagination.invalid_sort");
        }

        // The cursor is bound to its caller: Bob cannot page through Alice's list with it.
        using (var foreign = await SendAsync(HttpMethod.Get, $"{DevicesRoute}?pageSize=2&cursor={Uri.EscapeDataString(cursor)}", bobs.AccessToken))
        {
            await AssertProblemAsync(foreign, HttpStatusCode.BadRequest, "pagination.cursor_mismatch");
        }

        DeviceNames(await ListAsync(bobs.AccessToken)).ShouldBe(["Bob's phone"]);
    }

    [Fact]
    public async Task Revoked_expired_and_stale_stamp_devices_are_not_listed()
    {
        var alice = await CreateUserAsync(Email);
        var enrollment = await EnrollTotpAsync(alice.Id);
        var bob = await CreateUserAsync(OtherEmail);
        var now = Factory.Time.GetUtcNow();

        // Written straight to the table, so the Dapper query alone decides what is listed: one usable device and five that are not.
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            TrustedDevice Trust(Guid userId, string name, string stamp, DateTimeOffset trustedAt)
                => TrustedDevice.Trust(userId, RandomNumberGenerator.GetBytes(32), name, null, stamp, TrustedDeviceLifetime, trustedAt);

            var revoked = Trust(alice.Id, "Revoked", alice.SecurityStamp, now.AddDays(-1));
            revoked.Revoke(now.AddMinutes(-1));
            context.AddRange(
                Trust(alice.Id, "Usable", alice.SecurityStamp, now.AddDays(-1)),
                revoked,
                Trust(alice.Id, "Expired", alice.SecurityStamp, now.AddDays(-31)),
                Trust(alice.Id, "Expires now", alice.SecurityStamp, now - TrustedDeviceLifetime),
                Trust(alice.Id, "Stale stamp", "an-older-security-stamp", now.AddDays(-1)),
                Trust(bob.Id, "Bob's", bob.SecurityStamp, now.AddDays(-1)));
            await context.SaveChangesAsync(Ct);
        }

        // A session of Alice's through the challenge (without remembering), so the list runs as her.
        var session = await VerifyAsync(Email, enrollment, rememberDevice: false);

        var page = await ListAsync(session.AccessToken, "?includeTotalCount=true");
        DeviceNames(page).ShouldBe(["Usable"]);
        page.GetProperty("totalCount").GetInt64().ShouldBe(1);
        (await QueryAsync(context => context.Set<TrustedDevice>().CountAsync(Ct))).ShouldBe(6);
    }

    [Fact]
    public async Task Revoke_stops_the_device_and_is_404_for_others()
    {
        var alice = await CreateUserAsync(Email);
        var enrollment = await EnrollTotpAsync(alice.Id);
        var remembered = await RememberAsync(Email, enrollment);
        var deviceId = (await QueryAsync(context => context.Set<TrustedDevice>().AsNoTracking().SingleAsync(Ct))).Id;
        var bob = await SignInAsync();
        SignOut();

        // Anonymous: 401. Another user's device and an unknown id: the same 404, never 403.
        using (var anonymous = await Client.DeleteAsync($"{DevicesRoute}/{deviceId}", Ct))
        {
            anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var anonymousList = await Client.GetAsync(DevicesRoute, Ct))
        {
            anonymousList.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var foreign = await SendAsync(HttpMethod.Delete, $"{DevicesRoute}/{deviceId}", bob.AccessToken))
        {
            await AssertProblemAsync(foreign, HttpStatusCode.NotFound, "auth.trusted_device_not_found");
        }

        using (var unknown = await SendAsync(HttpMethod.Delete, $"{DevicesRoute}/{Guid.NewGuid()}", bob.AccessToken))
        {
            await AssertProblemAsync(unknown, HttpStatusCode.NotFound, "auth.trusted_device_not_found");
        }

        (await QueryAsync(context => context.Set<TrustedDevice>().AsNoTracking().SingleAsync(Ct))).RevokedAt.ShouldBeNull();

        // The owner revokes it (no step-up needed: the session's code is not what allows it); a second revoke is 204 too.
        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        var revokedAt = Factory.Time.GetUtcNow();
        using (var revoke = await SendAsync(HttpMethod.Delete, $"{DevicesRoute}/{deviceId}", remembered.AccessToken))
        {
            revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        Factory.Time.Advance(TimeSpan.FromSeconds(1));
        using (var again = await SendAsync(HttpMethod.Delete, $"{DevicesRoute}/{deviceId}", remembered.AccessToken))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await QueryAsync(context => context.Set<TrustedDevice>().AsNoTracking().SingleAsync(Ct))).RevokedAt.ShouldBe(revokedAt);
        var audits = await QueryAsync(context => context.Set<AuthAuditLog>().AsNoTracking()
            .Where(entry => entry.EventType == AuthAuditEvents.TrustedDeviceRevoked).ToListAsync(Ct));
        var audit = audits.ShouldHaveSingleItem();
        audit.UserId.ShouldBe(alice.Id);
        audit.SessionId.ShouldBe(remembered.SessionId);
        audit.Details.ShouldBe($$"""{"trustedDeviceId":"{{deviceId}}"}""");

        // The device no longer skips the challenge and is no longer listed.
        (await ChallengeOrTokensAsync(Email, remembered.DeviceToken)).ShouldBeTrue();
        (await ListAsync(remembered.AccessToken)).GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Device_token_is_never_logged_or_audited()
    {
        var logs = new RecordingLoggerProvider();
        await using var host = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILoggerFactory>();
            services.AddSingleton<ILoggerFactory>(_ => new LoggerFactory([logs]));
        }));
        using var client = host.CreateClient();
        var user = await CreateUserAsync(Email);
        var enrollment = await AuthTestHarness.EnrollTotpAsync(host.Services, user.Id, Ct);
        var unknownToken = host.Services.GetRequiredService<ISecureTokenService>().Generate().Value;

        // Every path the token takes: issued at verify, used at login, presented unknown, listed and revoked.
        var remembered = await RememberAsync(Email, enrollment, client: client);
        (await ChallengeOrTokensAsync(Email, remembered.DeviceToken, client: client)).ShouldBeFalse();
        (await ChallengeOrTokensAsync(Email, unknownToken, client: client)).ShouldBeTrue();
        var deviceId = (await QueryAsync(context => context.Set<TrustedDevice>().AsNoTracking().SingleAsync(Ct))).Id;
        (await ListAsync(remembered.AccessToken, client: client)).GetProperty("items").GetArrayLength().ShouldBe(1);
        using (var revoke = await SendAsync(HttpMethod.Delete, $"{DevicesRoute}/{deviceId}", remembered.AccessToken, client: client))
        {
            revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        string[] secrets = [remembered.DeviceToken, unknownToken];

        // No text column of any table holds either token (the device keeps only the hash) ...
        (await DatabaseTextScanner.ColumnsContainingAsync(Factory.Services, secrets, Ct)).ShouldBeEmpty();

        // ... the audit rows of every step exist and hold neither ...
        var audits = await QueryAsync(context => context.Set<AuthAuditLog>().AsNoTracking().ToListAsync(Ct));
        audits.Select(entry => entry.EventType).ShouldContain(AuthAuditEvents.TrustedDeviceAdded);
        audits.Select(entry => entry.EventType).ShouldContain(AuthAuditEvents.TrustedDeviceRevoked);
        foreach (var entry in audits)
        {
            string?[] texts = [entry.EventType, entry.FailureReason, entry.AttemptedIdentifier, entry.Details, entry.UserAgent, entry.IpAddress, entry.TraceId];
            texts.ShouldAllBe(text => text == null || !ContainsAny(text, secrets));
        }

        // ... and no log line of any level or category does, though the handlers ran and were logged.
        logs.Entries.ShouldContain(entry => entry.Contains("Completed VerifyMfaCommand", StringComparison.Ordinal));
        logs.Entries.ShouldContain(entry => entry.Contains("Completed LoginCommand", StringComparison.Ordinal));
        logs.Entries.ShouldContain(entry => entry.Contains("Completed RevokeTrustedDeviceCommand", StringComparison.Ordinal));
        logs.Entries.ShouldAllBe(entry => !ContainsAny(entry, secrets));
    }

    private static List<string> Amr(JsonWebToken token) => [.. token.Claims.Where(claim => claim.Type == "amr").Select(claim => claim.Value)];

    private static List<string?> DeviceNames(JsonElement page)
        => [.. page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("deviceName").GetString())];

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe(code);
    }

    /// <summary>Whether <paramref name="text"/> holds one of <paramref name="secrets"/>, ignoring case.</summary>
    private static bool ContainsAny(string text, IEnumerable<string> secrets)
        => secrets.Any(secret => Regex.IsMatch(text, Regex.Escape(secret), RegexOptions.IgnoreCase));

    /// <summary>
    /// Signs <paramref name="email"/> in with <paramref name="password"/> and <paramref name="deviceToken"/>: true when the answer is a
    /// challenge, false when it is the tokens of a session.
    /// </summary>
    private async Task<bool> ChallengeOrTokensAsync(string email, string deviceToken, string password = AuthTestHarness.Password, HttpClient? client = null)
    {
        using var response = await LoginAsync(email, password, deviceToken, client: client);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var challenged = body.GetProperty("mfaRequired").GetBoolean();
        body.TryGetProperty(challenged ? "accessToken" : "challengeToken", out _).ShouldBeFalse();
        return challenged;
    }

    /// <summary>Signs <paramref name="email"/> in through the challenge with the code of the moment and <c>rememberDevice</c>.</summary>
    private async Task<Verified> RememberAsync(string email, TotpEnrollment enrollment, string? deviceName = null, HttpClient? client = null)
    {
        var verified = await VerifyAsync(email, enrollment, rememberDevice: true, deviceName, client);
        verified.DeviceToken.ShouldNotBeNullOrEmpty();
        return verified;
    }

    private async Task<Verified> VerifyAsync(string email, TotpEnrollment enrollment, bool rememberDevice, string? deviceName = null, HttpClient? client = null)
    {
        client ??= Client;
        string challengeToken;
        using (var login = await LoginAsync(email, deviceName: deviceName, client: client))
        {
            login.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await login.Content.ReadFromJsonAsync<JsonElement>(Ct);
            body.GetProperty("mfaRequired").GetBoolean().ShouldBeTrue();
            challengeToken = body.GetProperty("challengeToken").GetString()!;
        }

        var code = enrollment.CodeAt(Factory.Time.GetUtcNow());
        using var verify = await PostAsync(client, VerifyRoute, new { challengeToken, code, rememberDevice });
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        verify.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        var tokens = await verify.Content.ReadFromJsonAsync<JsonElement>(Ct);
        if (!rememberDevice)
        {
            tokens.TryGetProperty("trustedDeviceToken", out _).ShouldBeFalse();
            tokens.TryGetProperty("trustedDeviceExpiresAt", out _).ShouldBeFalse();
            return new Verified(tokens.GetProperty("accessToken").GetString()!, tokens.GetProperty("sessionId").GetGuid(), string.Empty, default);
        }

        return new Verified(
            tokens.GetProperty("accessToken").GetString()!,
            tokens.GetProperty("sessionId").GetGuid(),
            tokens.GetProperty("trustedDeviceToken").GetString()!,
            tokens.GetProperty("trustedDeviceExpiresAt").GetDateTimeOffset());
    }

    private Task<HttpResponseMessage> LoginAsync(
        string email,
        string password = AuthTestHarness.Password,
        string? deviceToken = null,
        string? deviceName = null,
        HttpClient? client = null)
        => PostAsync(client ?? Client, LoginRoute, new { email, password, deviceName, trustedDeviceToken = deviceToken });

    /// <summary>Posts <paramref name="body"/> anonymously from an address no request used before, with <see cref="UserAgent"/>.</summary>
    private async Task<HttpResponseMessage> PostAsync(HttpClient client, string route, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body) };
        request.Headers.Add(TestClientAddressStartupFilter.HeaderName, $"198.51.100.{Interlocked.Increment(ref _addressCount)}");
        request.Headers.UserAgent.ParseAdd(UserAgent);
        return await client.SendAsync(request, Ct);
    }

    /// <summary>Sends an authenticated request with <paramref name="accessToken"/>, and <paramref name="body"/> as JSON when given.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string route, string accessToken, object? body = null, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(method, route).WithBearer(accessToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Add(TestClientAddressStartupFilter.HeaderName, $"198.51.100.{Interlocked.Increment(ref _addressCount)}");
        return await (client ?? Client).SendAsync(request, Ct);
    }

    private async Task<JsonElement> ListAsync(string accessToken, string query = "", HttpClient? client = null)
    {
        using var response = await SendAsync(HttpMethod.Get, DevicesRoute + query, accessToken, client: client);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private Task<User> FindUserAsync(Guid userId)
        => QueryAsync(context => context.Set<User>().AsNoTracking().SingleAsync(user => user.Id == userId, Ct));

    private Task<List<string>> AuditEventsAsync()
        => QueryAsync(context => context.Set<AuthAuditLog>().OrderBy(entry => entry.Id).Select(entry => entry.EventType).ToListAsync(Ct));

    private async Task<T> QueryAsync<T>(Func<AuthDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    /// <summary>What a verify answered: the session's access token and id, and the device token with its expiry when one was remembered.</summary>
    private sealed record Verified(string AccessToken, Guid SessionId, string DeviceToken, DateTimeOffset ExpiresAt);
}
