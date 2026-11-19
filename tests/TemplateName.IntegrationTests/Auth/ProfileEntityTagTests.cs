using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Domain.Roles;

namespace TemplateName.IntegrationTests.Auth;

/// <summary><c>ETag</c> and <c>If-Match</c> on the caller profile (ADR 0038): display name, locale and time zone only.</summary>
public sealed class ProfileEntityTagTests(IntegrationTestWebAppFactory factory) : AdminTestBase(factory)
{
    private const string MeRoute = "/api/v1/auth/me";

    [Fact]
    public async Task Get_returns_an_etag_that_a_matching_update_accepts_and_replaces()
    {
        await SignInAsync();
        var read = await ReadETagAsync();

        using var updated = await Client.PutJsonAsync(MeRoute, Profile("Alicia"), read, Ct);

        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var next = EntityTagRequests.ETagOf(updated);
        next.ShouldNotBe(read);
        (await ReadETagAsync()).ShouldBe(next);
    }

    [Fact]
    public async Task A_stale_etag_is_412_and_changes_nothing()
    {
        await SignInAsync();
        var read = await ReadETagAsync();
        using (var first = await Client.PutJsonAsync(MeRoute, Profile("First"), read, Ct))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var stale = await Client.PutJsonAsync(MeRoute, Profile("Second"), read, Ct);

        await AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency.precondition_failed");
        using var me = await Client.GetAsync(MeRoute, Ct);
        (await me.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("displayName").GetString().ShouldBe("First");
    }

    [Fact]
    public async Task Without_if_match_the_update_behaves_as_before()
    {
        await SignInAsync();

        using var updated = await Client.PutJsonAsync(MeRoute, Profile("Alicia"), null, Ct);

        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        EntityTagRequests.ETagOf(updated).ShouldBe(await ReadETagAsync());
    }

    [Fact]
    public async Task Star_matches_weak_is_412_and_malformed_is_400()
    {
        await SignInAsync();
        var read = await ReadETagAsync();

        using (var weak = await Client.PutJsonAsync(MeRoute, Profile("Weak"), "W/" + read, Ct))
        {
            await AssertProblemAsync(weak, HttpStatusCode.PreconditionFailed, "concurrency.precondition_failed");
        }

        using (var malformed = await Client.PutJsonAsync(MeRoute, Profile("Bad"), "not-quoted", Ct))
        {
            await AssertProblemAsync(malformed, HttpStatusCode.BadRequest, "request.malformed");
        }

        using var star = await Client.PutJsonAsync(MeRoute, Profile("Star"), "*", Ct);
        star.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_role_change_does_not_change_the_profile_etag()
    {
        var user = await SignInAsync();
        var read = await ReadETagAsync();
        var adminRoleId = await RoleIdAsync(SystemRoles.Admin);

        await ChangeUserAsync(user.UserId, (_, stored) => stored.AssignRole(adminRoleId, user.UserId, Factory.Time.GetUtcNow()));

        (await ReadETagAsync()).ShouldBe(read);
    }

    private static object Profile(string displayName) => new { displayName, locale = "en", timeZone = "UTC" };

    private async Task<string> ReadETagAsync()
    {
        using var response = await Client.GetAsync(MeRoute, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return EntityTagRequests.ETagOf(response);
    }
}
