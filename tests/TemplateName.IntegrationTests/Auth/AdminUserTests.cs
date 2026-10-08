using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.Authorization;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// The user administration routes <c>/api/v1/admin/auth/users/...</c> through the running host. The list is read through Dapper, which
/// bypasses the EF Core filters, so these tests also prove its SQL leaves soft-deleted users out.
/// </summary>
public sealed class AdminUserTests(IntegrationTestWebAppFactory factory) : AdminTestBase(factory)
{
    private const string UsersRoute = "/api/v1/admin/auth/users";
    private const string RefreshRoute = "/api/v1/auth/token/refresh";
    private const string ResetRoute = "/api/v1/auth/password/reset";
    private const string Password = AuthTestHarness.Password;
    private const string NewPassword = "a brand new passphrase";
    private const string ResetSubject = "Reset your password";

    private static readonly string[] AllUserPermissions =
    [
        AuthPermissions.UserView,
        AuthPermissions.UserCreate,
        AuthPermissions.UserLock,
        AuthPermissions.UserResetPassword,
        AuthPermissions.UserRevokeSessions,
        AuthPermissions.UserAssignRoles,
    ];

    /// <summary>Every route with the permission it needs and a valid body where it takes one.</summary>
    public static TheoryData<string, string, string, string?> Routes => new()
    {
        { "GET", UsersRoute, AuthPermissions.UserView, null },
        { "GET", UsersRoute + "/{id}", AuthPermissions.UserView, null },
        { "POST", UsersRoute, AuthPermissions.UserCreate, """{"email":"new@example.com","displayName":"New"}""" },
        { "POST", UsersRoute + "/{id}/lock", AuthPermissions.UserLock, null },
        { "POST", UsersRoute + "/{id}/unlock", AuthPermissions.UserLock, null },
        { "POST", UsersRoute + "/{id}/force-password-reset", AuthPermissions.UserResetPassword, null },
        { "POST", UsersRoute + "/{id}/revoke-sessions", AuthPermissions.UserRevokeSessions, null },
        { "PUT", UsersRoute + "/{id}/roles", AuthPermissions.UserAssignRoles, """{"roleIds":[]}""" },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Anonymous_is_401_and_signed_in_without_permission_is_403(string method, string route, string permission, string? body)
    {
        var target = await CreateUserAsync("target@example.com");
        var path = route.Replace("{id}", target.Id.ToString(), StringComparison.Ordinal);

        using (var anonymous = await SendAsync(method, path, body))
        {
            await AssertProblemAsync(anonymous, HttpStatusCode.Unauthorized, "http.401");
        }

        // Every other user administration permission, but not this route's.
        await SignInAsync([.. AllUserPermissions.Where(code => code != permission)], AuthTestHarness.DefaultLocale);
        using (var forbidden = await SendAsync(method, path, body))
        {
            await AssertProblemAsync(forbidden, HttpStatusCode.Forbidden, "http.403");
        }

        // Nothing happened to the target.
        var stored = await FindUserAsync("target@example.com");
        stored.Status.ShouldBe(UserStatus.Active);
        (await AdminAuditRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task List_is_paged_and_filtered()
    {
        var actor = await SignInAsync(AuthPermissions.UserView);
        var albert = await CreateUserLaterAsync("albert@example.com");
        var alfred = await CreateUserLaterAsync("alfred@example.com", suspended: true);
        var alice = await CreateUserLaterAsync("Alice@Example.com");
        var bob = await CreateUserLaterAsync("bob@example.com");
        var underscore = await CreateUserLaterAsync("x_y@example.com");
        var lookalike = await CreateUserLaterAsync("xay@example.com");

        // An email prefix (any case and surrounding spaces), sorted by email, two per page.
        var first = await GetPageAsync("?search=%20AL&sort=email&pageSize=2&includeTotalCount=true");
        Ids(first).ShouldBe([albert.Id, alfred.Id]);
        first.GetProperty("totalCount").GetInt64().ShouldBe(3);
        var second = await GetPageAsync($"?search=%20AL&sort=email&pageSize=2&cursor={first.GetProperty("nextCursor").GetString()}");
        Ids(second).ShouldBe([alice.Id]);
        second.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);

        // A cursor belongs to its filters.
        using (var mismatch = await Client.GetAsync($"{UsersRoute}?search=b&sort=email&pageSize=2&cursor={first.GetProperty("nextCursor").GetString()}", Ct))
        {
            await AssertProblemAsync(mismatch, HttpStatusCode.BadRequest, "pagination.cursor_mismatch");
        }

        // The status filter.
        Ids(await GetPageAsync("?status=Suspended")).ShouldBe([alfred.Id]);

        // The search is a literal prefix: "_" is not a wildcard.
        Ids(await GetPageAsync("?search=x_")).ShouldBe([underscore.Id]);

        // The default order is newest first; the harness's caller was created before the others.
        Ids(await GetPageAsync(string.Empty)).ShouldBe([lookalike.Id, underscore.Id, bob.Id, alice.Id, alfred.Id, albert.Id, actor.UserId]);

        // Exactly these members: never a hash, a stamp or a token.
        var item = (await GetPageAsync("?search=alice")).GetProperty("items").EnumerateArray().Single();
        item.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["id", "email", "displayName", "status", "emailConfirmed", "isLockedOut", "createdAt", "lastLoginAt"],
            ignoreOrder: true);
        item.GetProperty("email").GetString().ShouldBe("Alice@Example.com");
        item.GetProperty("status").GetString().ShouldBe("Active");

        using var badSort = await Client.GetAsync($"{UsersRoute}?sort=displayName", Ct);
        await AssertProblemAsync(badSort, HttpStatusCode.BadRequest, "pagination.invalid_sort");
    }

    [Fact]
    public async Task Soft_deleted_users_are_not_listed()
    {
        await SignInAsync(AuthPermissions.UserView);
        var kept = await CreateUserAsync("kept@example.com");
        var deleted = await CreateUserAsync("deleted@example.com");
        await ChangeUserAsync(deleted.Id, (context, user) => context.Remove(user));

        var page = await GetPageAsync("?search=&includeTotalCount=true&pageSize=100");

        Ids(page).ShouldContain(kept.Id);
        Ids(page).ShouldNotContain(deleted.Id);
        page.GetProperty("totalCount").GetInt64().ShouldBe(Ids(page).Count);
        Ids(await GetPageAsync("?search=deleted")).ShouldBeEmpty();
        Ids(await GetPageAsync("?search=deleted&sort=email")).ShouldBeEmpty();

        // The row is still there, only soft-deleted (a hard delete would prove nothing about the filter).
        (await QueryAsync(context => context.Set<User>().IgnoreQueryFilters().CountAsync(user => user.Id == deleted.Id && user.IsDeleted, Ct))).ShouldBe(1);

        using var get = await Client.GetAsync($"{UsersRoute}/{deleted.Id}", Ct);
        await AssertProblemAsync(get, HttpStatusCode.NotFound, "auth.user_not_found");
    }

    [Fact]
    public async Task Get_returns_the_user_with_roles_and_never_a_secret()
    {
        await SignInAsync(AuthPermissions.UserView);
        var target = await CreateUserAsync("target@example.com");

        using var response = await Client.GetAsync($"{UsersRoute}/{target.Id}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["id", "email", "emailConfirmed", "displayName", "locale", "timeZone", "status", "hasPassword", "isLockedOut", "lockoutEnd",
             "lastLoginAt", "passwordChangedAt", "createdAt", "roles"],
            ignoreOrder: true);
        body.GetProperty("id").GetGuid().ShouldBe(target.Id);
        body.GetProperty("hasPassword").GetBoolean().ShouldBeTrue();
        body.GetProperty("roles").EnumerateArray().Select(role => role.GetProperty("name").GetString()).ShouldBe([SystemRoles.User]);
        var text = await response.Content.ReadAsStringAsync(Ct);
        text.ShouldNotContain(target.SecurityStamp);
        text.ShouldNotContain(target.PasswordHash!);

        using var unknown = await Client.GetAsync($"{UsersRoute}/{Guid.NewGuid()}", Ct);
        await AssertProblemAsync(unknown, HttpStatusCode.NotFound, "auth.user_not_found");
    }

    [Fact]
    public async Task Lock_blocks_login_and_kills_refresh_tokens_and_unlock_restores_login()
    {
        await SignInAsync(AuthPermissions.UserLock);
        var target = await CreateUserAsync("target@example.com");
        var tokens = await LoginAsync("target@example.com", Password);

        // Five wrong passwords lock the account for sign-in as well (a login lockout, not a suspension).
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failed = await PostAnonymousAsync(LoginRoute, new { email = "target@example.com", password = "wrong password!" });
            failed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var locked = await Client.PostAsync($"{UsersRoute}/{target.Id}/lock", null, Ct))
        {
            locked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var suspended = await FindUserAsync("target@example.com");
        suspended.Status.ShouldBe(UserStatus.Suspended);
        suspended.SecurityStamp.ShouldNotBe(target.SecurityStamp);
        var session = await QueryAsync(context => context.Set<UserSession>().SingleAsync(candidate => candidate.Id == tokens.SessionId, Ct));
        session.RevokedReason.ShouldBe(SessionRevokedReason.AdminRevoked);
        using (var refresh = await PostAnonymousAsync(RefreshRoute, new { refreshToken = tokens.RefreshToken }))
        {
            await AssertProblemAsync(refresh, HttpStatusCode.Unauthorized, "auth.invalid_refresh_token");
        }

        // Past the login lockout (and the administrator's access token, which is renewed).
        Factory.Time.Advance(TimeSpan.FromMinutes(16));
        await RenewAccessTokenAsync();
        using (var login = await PostAnonymousAsync(LoginRoute, new { email = "target@example.com", password = Password }))
        {
            await AssertProblemAsync(login, HttpStatusCode.Forbidden, "auth.account_inactive");
        }

        // Locking again is not an error.
        using (var again = await Client.PostAsync($"{UsersRoute}/{target.Id}/lock", null, Ct))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // Five more wrong passwords while suspended: unlock must also end this login lockout.
        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failed = await PostAnonymousAsync(LoginRoute, new { email = "target@example.com", password = "wrong password!" });
            failed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await FindUserAsync("target@example.com")).IsLockedOut(Factory.Time.GetUtcNow()).ShouldBeTrue();
        using (var unlocked = await Client.PostAsync($"{UsersRoute}/{target.Id}/unlock", null, Ct))
        {
            unlocked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        await LoginAsync("target@example.com", Password);
    }

    [Fact]
    public async Task Lock_refuses_the_callers_own_account()
    {
        var actor = await SignInAsync(AuthPermissions.UserLock);

        using var response = await Client.PostAsync($"{UsersRoute}/{actor.UserId}/lock", null, Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "auth.cannot_lock_self");
        (await QueryAsync(context => context.Set<User>().SingleAsync(user => user.Id == actor.UserId, Ct))).Status.ShouldBe(UserStatus.Active);
    }

    [Fact]
    public async Task Admin_created_user_receives_a_set_password_email_and_can_log_in_after_reset()
    {
        await SignInAsync(AuthPermissions.UserCreate, AuthPermissions.UserView);

        using var created = await Client.PostAsJsonAsync(
            UsersRoute,
            new { email = " New.Person@Example.com ", displayName = "New Person", locale = "ms" },
            Ct);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        created.Headers.Location!.OriginalString.ShouldBe($"{UsersRoute}/{id}");
        var user = await FindUserAsync("new.person@example.com");
        user.Id.ShouldBe(id);
        user.PasswordHash.ShouldBeNull();
        user.EmailConfirmed.ShouldBeFalse();
        user.Locale.ShouldBe("ms");

        // No password yet: login answers like an unknown email (Ruling R3).
        using (var beforeReset = await PostAnonymousAsync(LoginRoute, new { email = "new.person@example.com", password = NewPassword }))
        {
            await AssertProblemAsync(beforeReset, HttpStatusCode.Unauthorized, "auth.invalid_credentials");
        }

        // The same address in another spelling is taken.
        using (var duplicate = await Client.PostAsJsonAsync(UsersRoute, new { email = "NEW.PERSON@example.com", displayName = "Again" }, Ct))
        {
            await AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "auth.email_taken");
        }

        await DispatchOutboxAsync();
        var email = Factory.EmailSender.Sent.ShouldHaveSingleItem();
        email.To.ShouldBe("New.Person@Example.com");
        email.Subject.ShouldBe(ResetSubject);
        var token = Factory.EmailSender.LastLinkToken("New.Person@Example.com");
        var contents = await QueryAsync(context => context.Set<OutboxMessage>().Select(message => message.Content).ToListAsync(Ct));
        contents.ShouldAllBe(content => !content.Contains(token));

        using (var reset = await PostAnonymousAsync(ResetRoute, new { token, newPassword = NewPassword }))
        {
            reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        await LoginAsync("new.person@example.com", NewPassword);
        using var details = await Client.GetAsync($"{UsersRoute}/{id}", Ct);
        var body = await details.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("emailConfirmed").GetBoolean().ShouldBeTrue();
        body.GetProperty("hasPassword").GetBoolean().ShouldBeTrue();
        body.GetProperty("roles").EnumerateArray().Select(role => role.GetProperty("name").GetString()).ShouldBe([SystemRoles.User]);
    }

    [Fact]
    public async Task Create_validates_the_body_and_the_role_ids()
    {
        await SignInAsync(AuthPermissions.UserCreate, AuthPermissions.UserAssignRoles);

        using (var invalid = await Client.PostAsJsonAsync(UsersRoute, new { email = "not an email", displayName = "" }, Ct))
        {
            invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var body = await invalid.Content.ReadFromJsonAsync<JsonElement>(Ct);
            body.GetProperty("code").GetString().ShouldBe("validation.failed");
        }

        var unknown = Guid.NewGuid();
        using (var unknownRole = await Client.PostAsJsonAsync(UsersRoute, new { email = "a@example.com", displayName = "A", roleIds = new[] { unknown } }, Ct))
        {
            await AssertProblemAsync(unknownRole, HttpStatusCode.NotFound, "auth.role_not_found");
        }

        // A soft-deleted role is unknown too.
        var archivedId = await CreateDeletedRoleAsync();
        using (var deletedRole = await Client.PostAsJsonAsync(UsersRoute, new { email = "a@example.com", displayName = "A", roleIds = new[] { archivedId } }, Ct))
        {
            await AssertProblemAsync(deletedRole, HttpStatusCode.NotFound, "auth.role_not_found");
        }

        // Only a SuperAdmin may create a SuperAdmin.
        var superAdminId = await RoleIdAsync(SystemRoles.SuperAdmin);
        using (var superAdmin = await Client.PostAsJsonAsync(UsersRoute, new { email = "a@example.com", displayName = "A", roleIds = new[] { superAdminId } }, Ct))
        {
            await AssertProblemAsync(superAdmin, HttpStatusCode.Forbidden, "auth.cannot_manage_super_admin");
        }

        (await QueryAsync(context => context.Set<User>().CountAsync(user => user.NormalizedEmail == "A@EXAMPLE.COM", Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Force_password_reset_revokes_sessions_and_sends_email()
    {
        await SignInAsync(AuthPermissions.UserResetPassword);
        var target = await CreateUserAsync("target@example.com");
        var tokens = await LoginAsync("target@example.com", Password);

        using (var forced = await Client.PostAsync($"{UsersRoute}/{target.Id}/force-password-reset", null, Ct))
        {
            forced.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            (await forced.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();
        }

        using (var refresh = await PostAnonymousAsync(RefreshRoute, new { refreshToken = tokens.RefreshToken }))
        {
            await AssertProblemAsync(refresh, HttpStatusCode.Unauthorized, "auth.invalid_refresh_token");
        }

        var session = await QueryAsync(context => context.Set<UserSession>().SingleAsync(candidate => candidate.Id == tokens.SessionId, Ct));
        session.RevokedReason.ShouldBe(SessionRevokedReason.AdminRevoked);

        await DispatchOutboxAsync();
        Factory.EmailSender.Sent.ShouldHaveSingleItem().Subject.ShouldBe(ResetSubject);
        var token = Factory.EmailSender.LastLinkToken("target@example.com");
        using (var reset = await PostAnonymousAsync(ResetRoute, new { token, newPassword = NewPassword }))
        {
            reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        await LoginAsync("target@example.com", NewPassword);
    }

    [Fact]
    public async Task Revoke_sessions_ends_every_session_of_the_user()
    {
        await SignInAsync(AuthPermissions.UserRevokeSessions);
        var target = await CreateUserAsync("target@example.com");
        var laptop = await LoginAsync("target@example.com", Password);
        var phone = await LoginAsync("target@example.com", Password);

        using (var revoked = await Client.PostAsync($"{UsersRoute}/{target.Id}/revoke-sessions", null, Ct))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        foreach (var tokens in new[] { laptop, phone })
        {
            using var refresh = await PostAnonymousAsync(RefreshRoute, new { refreshToken = tokens.RefreshToken });
            await AssertProblemAsync(refresh, HttpStatusCode.Unauthorized, "auth.invalid_refresh_token");
        }

        // The account itself is untouched: the user signs in again.
        await LoginAsync("target@example.com", Password);
    }

    [Fact]
    public async Task Assigning_a_role_grants_access_on_the_next_request()
    {
        // The actor holds every permission of Admin, so it may grant the role (the grant rule).
        await SignInAsync([AuthPermissions.UserAssignRoles, .. AuthSeeder.AdminDefaultPermissions], AuthTestHarness.DefaultLocale);
        var target = await CreateUserAsync("target@example.com");
        var tokens = await LoginAsync("target@example.com", Password);

        // The target's (empty) permission set is now cached for 30 seconds.
        using (var before = await GetAsAsync(tokens.AccessToken, UsersRoute))
        {
            before.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var adminId = await RoleIdAsync(SystemRoles.Admin);
        var userId = await RoleIdAsync(SystemRoles.User);
        using (var assigned = await Client.PutAsJsonAsync($"{UsersRoute}/{target.Id}/roles", new { roleIds = new[] { adminId, userId } }, Ct))
        {
            assigned.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // No clock movement: the assignment removed the cached set, so the very next request sees the Admin role.
        using (var after = await GetAsAsync(tokens.AccessToken, UsersRoute))
        {
            after.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // And taking it away works the same way.
        using (var removed = await Client.PutAsJsonAsync($"{UsersRoute}/{target.Id}/roles", new { roleIds = new[] { userId } }, Ct))
        {
            removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var afterRemoval = await GetAsAsync(tokens.AccessToken, UsersRoute);
        afterRemoval.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Assign_roles_and_create_grant_only_roles_whose_permissions_the_actor_holds()
    {
        var actor = await SignInAsync([AuthPermissions.UserAssignRoles, AuthPermissions.UserCreate, AuthPermissions.UserView], AuthTestHarness.DefaultLocale);
        var target = await CreateUserAsync("target@example.com");
        var userRoleId = await RoleIdAsync(SystemRoles.User);
        var powerfulId = await CreateRoleAsync("Powerful", AuthPermissions.RoleManage, AuthPermissions.UserView);
        var viewerId = await CreateRoleAsync("Viewer", AuthPermissions.UserView);

        // A role granting auth.role.manage, which the actor lacks: not to another user, not to themselves, not on a new account.
        using (var toTarget = await Client.PutAsJsonAsync($"{UsersRoute}/{target.Id}/roles", new { roleIds = new[] { userRoleId, powerfulId } }, Ct))
        {
            await AssertProblemAsync(toTarget, HttpStatusCode.Forbidden, "auth.permission_grant_not_allowed");
        }

        using (var toSelf = await Client.PutAsJsonAsync($"{UsersRoute}/{actor.UserId}/roles", new { roleIds = new[] { powerfulId } }, Ct))
        {
            await AssertProblemAsync(toSelf, HttpStatusCode.Forbidden, "auth.permission_grant_not_allowed");
        }

        using (var created = await Client.PostAsJsonAsync(UsersRoute, new { email = "powerful@example.com", displayName = "P", roleIds = new[] { powerfulId } }, Ct))
        {
            await AssertProblemAsync(created, HttpStatusCode.Forbidden, "auth.permission_grant_not_allowed");
        }

        (await RoleIdsOfAsync(target.Id)).ShouldBe([userRoleId]);
        (await RoleIdsOfAsync(actor.UserId)).ShouldNotContain(powerfulId);
        (await QueryAsync(context => context.Set<User>().CountAsync(user => user.NormalizedEmail == "POWERFUL@EXAMPLE.COM", Ct))).ShouldBe(0);
        (await AdminAuditRowsAsync()).ShouldBeEmpty();

        // A role whose every permission the actor holds may be granted both ways.
        using (var allowed = await Client.PutAsJsonAsync($"{UsersRoute}/{target.Id}/roles", new { roleIds = new[] { userRoleId, viewerId } }, Ct))
        {
            allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using (var createdViewer = await Client.PostAsJsonAsync(UsersRoute, new { email = "viewer@example.com", displayName = "V", roleIds = new[] { viewerId } }, Ct))
        {
            createdViewer.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        (await RoleIdsOfAsync(target.Id)).ShouldBe([userRoleId, viewerId], ignoreOrder: true);
    }

    [Fact]
    public async Task Super_admin_may_grant_any_role()
    {
        var actor = await SignInAsync(AuthPermissions.UserAssignRoles, AuthPermissions.UserCreate);
        await GrantRoleAsync(actor.UserId, SystemRoles.SuperAdmin);
        var target = await CreateUserAsync("target@example.com");
        var powerfulId = await CreateRoleAsync("Powerful", AuthPermissions.RoleManage, AuthPermissions.AuditView);

        using (var assigned = await Client.PutAsJsonAsync($"{UsersRoute}/{target.Id}/roles", new { roleIds = new[] { powerfulId } }, Ct))
        {
            assigned.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using (var created = await Client.PostAsJsonAsync(UsersRoute, new { email = "powerful@example.com", displayName = "P", roleIds = new[] { powerfulId } }, Ct))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        (await RoleIdsOfAsync(target.Id)).ShouldBe([powerfulId]);
    }

    [Fact]
    public async Task Admin_cannot_manage_a_super_admin_or_grant_the_role()
    {
        var actor = await SignInAsync([.. AllUserPermissions], AuthTestHarness.DefaultLocale);
        var superAdmin = await CreateUserAsync("root@example.com");
        await GrantRoleAsync(superAdmin.Id, SystemRoles.SuperAdmin);
        var superAdminRoleId = await RoleIdAsync(SystemRoles.SuperAdmin);

        foreach (var action in new[] { "lock", "unlock", "force-password-reset", "revoke-sessions" })
        {
            using var response = await Client.PostAsync($"{UsersRoute}/{superAdmin.Id}/{action}", null, Ct);
            await AssertProblemAsync(response, HttpStatusCode.Forbidden, "auth.cannot_manage_super_admin");
        }

        using (var demote = await Client.PutAsJsonAsync($"{UsersRoute}/{superAdmin.Id}/roles", new { roleIds = Array.Empty<Guid>() }, Ct))
        {
            await AssertProblemAsync(demote, HttpStatusCode.Forbidden, "auth.cannot_manage_super_admin");
        }

        using (var selfGrant = await Client.PutAsJsonAsync($"{UsersRoute}/{actor.UserId}/roles", new { roleIds = new[] { superAdminRoleId } }, Ct))
        {
            await AssertProblemAsync(selfGrant, HttpStatusCode.Forbidden, "auth.cannot_manage_super_admin");
        }

        (await FindUserAsync("root@example.com")).Status.ShouldBe(UserStatus.Active);
        (await AdminAuditRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Last_super_admin_cannot_lose_the_role()
    {
        var actor = await SignInAsync(AuthPermissions.UserAssignRoles, AuthPermissions.UserLock);
        await GrantRoleAsync(actor.UserId, SystemRoles.SuperAdmin);
        var superAdminRoleId = await RoleIdAsync(SystemRoles.SuperAdmin);

        using (var demoteSelf = await Client.PutAsJsonAsync($"{UsersRoute}/{actor.UserId}/roles", new { roleIds = Array.Empty<Guid>() }, Ct))
        {
            await AssertProblemAsync(demoteSelf, HttpStatusCode.Conflict, "auth.last_super_admin");
        }

        // With a second active SuperAdmin the role may go; a suspended one does not count.
        var second = await CreateUserAsync("second@example.com", suspended: true);
        await GrantRoleAsync(second.Id, SystemRoles.SuperAdmin);
        using (var stillLast = await Client.PutAsJsonAsync($"{UsersRoute}/{actor.UserId}/roles", new { roleIds = Array.Empty<Guid>() }, Ct))
        {
            await AssertProblemAsync(stillLast, HttpStatusCode.Conflict, "auth.last_super_admin");
        }

        using (var unlock = await Client.PostAsync($"{UsersRoute}/{second.Id}/unlock", null, Ct))
        {
            unlock.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using (var demoted = await Client.PutAsJsonAsync($"{UsersRoute}/{actor.UserId}/roles", new { roleIds = Array.Empty<Guid>() }, Ct))
        {
            demoted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await QueryAsync(context => context.Set<UserRole>().CountAsync(role => role.UserId == actor.UserId && role.RoleId == superAdminRoleId, Ct)))
            .ShouldBe(0);
    }

    [Fact]
    public async Task Audit_rows_record_actor_and_target()
    {
        var actor = await SignInAsync([.. AllUserPermissions], AuthTestHarness.DefaultLocale);
        var target = await CreateUserAsync("target@example.com");
        var userRoleId = await RoleIdAsync(SystemRoles.User);

        using (var created = await Client.PostAsJsonAsync(UsersRoute, new { email = "created@example.com", displayName = "Created" }, Ct))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        foreach (var action in new[] { "lock", "unlock", "force-password-reset", "revoke-sessions" })
        {
            using var response = await Client.PostAsync($"{UsersRoute}/{target.Id}/{action}", null, Ct);
            response.IsSuccessStatusCode.ShouldBeTrue(action);
        }

        using (var assigned = await Client.PutAsJsonAsync($"{UsersRoute}/{target.Id}/roles", new { roleIds = new[] { userRoleId } }, Ct))
        {
            assigned.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var createdUser = await FindUserAsync("created@example.com");
        var rows = await AdminAuditRowsAsync();
        rows.Select(row => (row.EventType, row.UserId)).ShouldBe(
        [
            (AuthAuditEvents.AdminUserCreated, (Guid?)createdUser.Id),
            (AuthAuditEvents.AdminUserLocked, target.Id),
            (AuthAuditEvents.AdminUserUnlocked, target.Id),
            (AuthAuditEvents.AdminPasswordResetForced, target.Id),
            (AuthAuditEvents.AdminSessionsRevoked, target.Id),
            (AuthAuditEvents.AdminRolesAssigned, target.Id),
        ]);
        foreach (var row in rows)
        {
            row.Succeeded.ShouldBeTrue();
            using var details = JsonDocument.Parse(row.Details!);
            details.RootElement.GetProperty("actorId").GetGuid().ShouldBe(actor.UserId);
            row.TraceId.ShouldNotBeNull();
        }
    }

    private static List<Guid> Ids(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())];

    private async Task<User> CreateUserLaterAsync(string email, bool suspended = false)
    {
        Factory.Time.Advance(TimeSpan.FromSeconds(1));
        return await CreateUserAsync(email, suspended: suspended);
    }

    private async Task<JsonElement> GetPageAsync(string query)
    {
        using var response = await Client.GetAsync(UsersRoute + query, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task DispatchOutboxAsync()
    {
        var dispatcher = Factory.Services.GetRequiredService<OutboxDispatcher<AuthDbContext>>();
        int claimed;
        do
        {
            claimed = await dispatcher.ProcessBatchAsync(Ct);
        }
        while (claimed > 0);
    }

    private async Task<Guid> CreateDeletedRoleAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var role = Role.Create("Archived", "Soft-deleted by the test", Factory.Time.GetUtcNow()).Value;
        context.Add(role);
        await context.SaveChangesAsync(Ct);
        context.Remove(role);
        await context.SaveChangesAsync(Ct);
        return role.Id;
    }

    private async Task<Guid> CreateRoleAsync(string name, params string[] permissionCodes)
    {
        var permissionIds = new List<Guid>();
        foreach (var code in permissionCodes)
        {
            permissionIds.Add(await PermissionIdAsync(code));
        }

        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var role = Role.Create(name, $"The {name} role.", Factory.Time.GetUtcNow()).Value;
        role.SetPermissions(permissionIds);
        context.Add(role);
        await context.SaveChangesAsync(Ct);
        return role.Id;
    }

    private Task<List<Guid>> RoleIdsOfAsync(Guid userId)
        => QueryAsync(context => context.Set<UserRole>().Where(role => role.UserId == userId).Select(role => role.RoleId).ToListAsync(Ct));

    private Task<User> FindUserAsync(string email)
        => QueryAsync(context => context.Set<User>().SingleAsync(user => user.NormalizedEmail == User.NormalizeEmail(email), Ct));

    private Task<List<AuthAuditLog>> AdminAuditRowsAsync()
        => QueryAsync(context => context.Set<AuthAuditLog>()
            .Where(entry => entry.EventType.StartsWith("auth.admin_"))
            .OrderBy(entry => entry.Id)
            .ToListAsync(Ct));
}
