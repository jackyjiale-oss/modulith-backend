using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.TrustedDevices;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// The administrator's MFA reset, <c>POST /api/v1/admin/auth/users/{id}/reset-mfa</c>, through the running host (plan decision D17),
/// and the <c>mfaEnabled</c> member of <c>GET /api/v1/admin/auth/users/{id}</c>. The target signs in through the real login and verify
/// routes, every anonymous request from its own client address so the strict limit never decides an outcome by accident.
/// </summary>
public sealed class AdminMfaTests(IntegrationTestWebAppFactory factory) : AdminTestBase(factory)
{
    private const string UsersRoute = "/api/v1/admin/auth/users";
    private const string VerifyRoute = "/api/v1/auth/mfa/verify";
    private const string RefreshRoute = "/api/v1/auth/token/refresh";
    private const string TargetEmail = "target@example.com";
    private const string Password = AuthTestHarness.Password;

    private int _addressCount;

    [Fact]
    public async Task Anonymous_is_401_and_without_permission_is_403()
    {
        var target = await CreateUserAsync(TargetEmail);
        await EnrollTotpAsync(target.Id);

        using (var anonymous = await Client.PostAsync(ResetRoute(target.Id), null, Ct))
        {
            await AssertProblemAsync(anonymous, HttpStatusCode.Unauthorized, "http.401");
        }

        // Four of the other user administration permissions (view, lock, reset password, revoke sessions), but not this one.
        await SignInAsync(AuthPermissions.UserView, AuthPermissions.UserLock, AuthPermissions.UserResetPassword, AuthPermissions.UserRevokeSessions);
        using (var forbidden = await Client.PostAsync(ResetRoute(target.Id), null, Ct))
        {
            await AssertProblemAsync(forbidden, HttpStatusCode.Forbidden, "http.403");
        }

        (await QueryAsync(context => context.Set<UserMfaMethod>().CountAsync(Ct))).ShouldBe(1);
        (await MfaResetAuditRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Reset_lets_the_user_sign_in_with_the_password_alone_and_ends_every_session()
    {
        var target = await CreateUserAsync(TargetEmail);
        var laptop = await LoginAsync(TargetEmail, Password);
        var phone = await LoginAsync(TargetEmail, Password);
        await EnrollTotpAsync(target.Id);
        var stamp = (await FindUserAsync(target.Id)).SecurityStamp;
        await SignInAsync(AuthPermissions.UserResetMfa);

        using (var reset = await Client.PostAsync(ResetRoute(target.Id), null, Ct))
        {
            reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            (await reset.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();
        }

        // The method and every recovery code are gone and the stamp rotated.
        (await QueryAsync(context => context.Set<UserMfaMethod>().CountAsync(Ct))).ShouldBe(0);
        (await QueryAsync(context => context.Set<RecoveryCode>().CountAsync(Ct))).ShouldBe(0);
        (await FindUserAsync(target.Id)).SecurityStamp.ShouldNotBe(stamp);

        // Every session of the user ended with AdminRevoked: neither refresh token works.
        foreach (var tokens in new[] { laptop, phone })
        {
            using var refresh = await PostAnonymousAsync(RefreshRoute, new { refreshToken = tokens.RefreshToken });
            await AssertProblemAsync(refresh, HttpStatusCode.Unauthorized, "auth.invalid_refresh_token");
            var session = await QueryAsync(context => context.Set<UserSession>().SingleAsync(candidate => candidate.Id == tokens.SessionId, Ct));
            session.RevokedReason.ShouldBe(SessionRevokedReason.AdminRevoked);
        }

        // The password alone signs in again: tokens, no challenge.
        using var login = await PostFromNewAddressAsync(LoginRoute, new { email = TargetEmail, password = Password });
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("mfaRequired").GetBoolean().ShouldBeFalse();
        body.GetProperty("accessToken").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Reset_forgets_trusted_devices_and_pending_challenges()
    {
        var target = await CreateUserAsync(TargetEmail);
        var enrollment = await EnrollTotpAsync(target.Id);
        var deviceToken = await RememberDeviceAsync(enrollment);
        var pendingChallenge = await ChallengeAsync();
        await SignInAsync(AuthPermissions.UserResetMfa);

        using (var reset = await Client.PostAsync(ResetRoute(target.Id), null, Ct))
        {
            reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // The user turns two-step verification on again (a new authenticator, the same account).
        var next = await EnrollTotpAsync(target.Id);

        // The challenge issued before the reset is dead, even with a valid code of the new authenticator.
        using (var verify = await PostFromNewAddressAsync(VerifyRoute, new { challengeToken = pendingChallenge, code = next.CodeAt(Factory.Time.GetUtcNow()) }))
        {
            await AssertProblemAsync(verify, HttpStatusCode.BadRequest, "auth.invalid_mfa_challenge");
        }

        // The remembered device was bound to the old security stamp: the new authenticator gets its challenge anyway.
        using var login = await PostFromNewAddressAsync(LoginRoute, new { email = TargetEmail, password = Password, trustedDeviceToken = deviceToken });
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("mfaRequired").GetBoolean().ShouldBeTrue();
        (await QueryAsync(context => context.Set<TrustedDevice>().CountAsync(Ct))).ShouldBe(1);
    }

    [Fact]
    public async Task Reset_without_mfa_answers_204_and_changes_nothing()
    {
        var target = await CreateUserAsync(TargetEmail);
        var tokens = await LoginAsync(TargetEmail, Password);
        var stamp = (await FindUserAsync(target.Id)).SecurityStamp;
        await SignInAsync(AuthPermissions.UserResetMfa);

        using (var reset = await Client.PostAsync(ResetRoute(target.Id), null, Ct))
        {
            reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await FindUserAsync(target.Id)).SecurityStamp.ShouldBe(stamp);
        var session = await QueryAsync(context => context.Set<UserSession>().SingleAsync(candidate => candidate.Id == tokens.SessionId, Ct));
        session.RevokedAt.ShouldBeNull();
        (await MfaResetAuditRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Reset_of_a_pending_setup_only_removes_it()
    {
        var target = await CreateUserAsync(TargetEmail);
        var tokens = await LoginAsync(TargetEmail, Password);
        var stamp = (await FindUserAsync(target.Id)).SecurityStamp;
        await ChangeUserAsync(target.Id, (context, user) => context.Add(UserMfaMethod.StartTotp(user.Id, "Phone", "protected", Factory.Time.GetUtcNow())));
        await SignInAsync(AuthPermissions.UserResetMfa);

        using (var reset = await Client.PostAsync(ResetRoute(target.Id), null, Ct))
        {
            reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await QueryAsync(context => context.Set<UserMfaMethod>().CountAsync(Ct))).ShouldBe(0);
        (await FindUserAsync(target.Id)).SecurityStamp.ShouldBe(stamp);
        var session = await QueryAsync(context => context.Set<UserSession>().SingleAsync(candidate => candidate.Id == tokens.SessionId, Ct));
        session.RevokedAt.ShouldBeNull();
        (await MfaResetAuditRowsAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Reset_refuses_oneself_unknown_users_and_super_admins_for_an_admin()
    {
        var actor = await SignInAsync(AuthPermissions.UserResetMfa);
        var superAdmin = await CreateUserAsync("root@example.com");
        await EnrollTotpAsync(superAdmin.Id);
        await GrantRoleAsync(superAdmin.Id, SystemRoles.SuperAdmin);
        await EnrollTotpAsync(actor.UserId);

        using (var own = await Client.PostAsync(ResetRoute(actor.UserId), null, Ct))
        {
            await AssertProblemAsync(own, HttpStatusCode.BadRequest, "auth.cannot_reset_own_mfa");
        }

        using (var unknown = await Client.PostAsync(ResetRoute(Guid.NewGuid()), null, Ct))
        {
            await AssertProblemAsync(unknown, HttpStatusCode.NotFound, "auth.user_not_found");
        }

        using (var forbidden = await Client.PostAsync(ResetRoute(superAdmin.Id), null, Ct))
        {
            await AssertProblemAsync(forbidden, HttpStatusCode.Forbidden, "auth.cannot_manage_super_admin");
        }

        // A soft-deleted user is unknown too.
        var deleted = await CreateUserAsync("deleted@example.com");
        await EnrollTotpAsync(deleted.Id);
        await ChangeUserAsync(deleted.Id, (context, user) => context.Remove(user));
        using (var gone = await Client.PostAsync(ResetRoute(deleted.Id), null, Ct))
        {
            await AssertProblemAsync(gone, HttpStatusCode.NotFound, "auth.user_not_found");
        }

        (await QueryAsync(context => context.Set<UserMfaMethod>().CountAsync(Ct))).ShouldBe(3);
        (await MfaResetAuditRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Get_user_shows_mfa_enabled()
    {
        await SignInAsync(AuthPermissions.UserView);
        var target = await CreateUserAsync(TargetEmail);

        (await MfaEnabledAsync(target.Id)).ShouldBeFalse();

        // A setup nobody confirmed does not count.
        await ChangeUserAsync(target.Id, (context, user) => context.Add(UserMfaMethod.StartTotp(user.Id, "Phone", "protected", Factory.Time.GetUtcNow())));
        (await MfaEnabledAsync(target.Id)).ShouldBeFalse();

        await QueryAsync(async context =>
        {
            context.RemoveRange(await context.Set<UserMfaMethod>().ToListAsync(Ct));
            await context.SaveChangesAsync(Ct);
            return 0;
        });
        await EnrollTotpAsync(target.Id);
        (await MfaEnabledAsync(target.Id)).ShouldBeTrue();
    }

    [Fact]
    public async Task Admin_role_gets_reset_mfa_by_default()
    {
        // The seeder ran on a fresh database: the Admin role holds the code, SuperAdmin holds it too, User does not.
        (await GrantedCodesAsync(SystemRoles.Admin)).ShouldContain(AuthPermissions.UserResetMfa);
        (await GrantedCodesAsync(SystemRoles.SuperAdmin)).ShouldContain(AuthPermissions.UserResetMfa);
        (await GrantedCodesAsync(SystemRoles.User)).ShouldNotContain(AuthPermissions.UserResetMfa);
    }

    [Fact]
    public async Task Audit_row_records_actor_and_target()
    {
        var target = await CreateUserAsync(TargetEmail);
        await EnrollTotpAsync(target.Id);
        var actor = await SignInAsync(AuthPermissions.UserResetMfa);

        using (var reset = await Client.PostAsync(ResetRoute(target.Id), null, Ct))
        {
            reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var row = (await MfaResetAuditRowsAsync()).ShouldHaveSingleItem();
        row.EventType.ShouldBe("auth.admin_mfa_reset");
        row.Succeeded.ShouldBeTrue();
        row.UserId.ShouldBe(target.Id);
        row.Details.ShouldBe($$"""{"actorId":"{{actor.UserId}}"}""");
    }

    private static string ResetRoute(Guid userId) => $"{UsersRoute}/{userId}/reset-mfa";

    private async Task<bool> MfaEnabledAsync(Guid userId)
    {
        using var response = await Client.GetAsync($"{UsersRoute}/{userId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("mfaEnabled").GetBoolean();
    }

    private async Task<string> ChallengeAsync()
    {
        using var login = await PostFromNewAddressAsync(LoginRoute, new { email = TargetEmail, password = Password });
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("mfaRequired").GetBoolean().ShouldBeTrue();
        return body.GetProperty("challengeToken").GetString()!;
    }

    private async Task<string> RememberDeviceAsync(TotpEnrollment enrollment)
    {
        var challengeToken = await ChallengeAsync();
        using var verify = await PostFromNewAddressAsync(
            VerifyRoute,
            new { challengeToken, code = enrollment.CodeAt(Factory.Time.GetUtcNow()), rememberDevice = true });
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await verify.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("trustedDeviceToken").GetString()!;
    }

    /// <summary>Posts <paramref name="body"/> anonymously from a client address no request used before.</summary>
    private async Task<HttpResponseMessage> PostFromNewAddressAsync(string route, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body) };
        request.Headers.Add(TestClientAddressStartupFilter.HeaderName, $"198.51.100.{Interlocked.Increment(ref _addressCount)}");
        using var anonymous = Factory.CreateClient();
        return await anonymous.SendAsync(request, Ct);
    }

    private Task<User> FindUserAsync(Guid userId)
        => QueryAsync(context => context.Set<User>().AsNoTracking().SingleAsync(user => user.Id == userId, Ct));

    private Task<List<AuthAuditLog>> MfaResetAuditRowsAsync()
        => QueryAsync(context => context.Set<AuthAuditLog>()
            .Where(entry => entry.EventType == "auth.admin_mfa_reset")
            .OrderBy(entry => entry.Id)
            .ToListAsync(Ct));

    private Task<List<string>> GrantedCodesAsync(string roleName)
        => QueryAsync(async context =>
        {
            var grantee = await context.Set<Role>().AsNoTracking().Include(role => role.Permissions).SingleAsync(role => role.Name == roleName, Ct);
            var permissionIds = grantee.Permissions.Select(grant => grant.PermissionId).ToList();
            return await context.Set<Permission>()
                .Where(permission => permissionIds.Contains(permission.Id))
                .Select(permission => permission.Code)
                .ToListAsync(Ct);
        });
}
