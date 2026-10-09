using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// <c>GET /api/v1/admin/auth/audit-logs</c> through the running host. The rows are inserted straight into <c>auth.AuthAuditLogs</c>
/// with chosen timestamps; the list is read through Dapper, so its paging is proved against SQL Server, ties included.
/// </summary>
public sealed class AuditLogTests(IntegrationTestWebAppFactory factory) : AdminTestBase(factory)
{
    private const string AuditRoute = "/api/v1/admin/auth/audit-logs";
    private const string RolesRoute = "/api/v1/admin/auth/roles";

    private static readonly DateTimeOffset Start = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private static readonly string[] AllOtherAdminPermissions =
    [
        AuthPermissions.UserView,
        AuthPermissions.RoleView,
        AuthPermissions.RoleManage,
        AuthPermissions.PermissionView,
    ];

    [Fact]
    public async Task Requires_audit_view_permission()
    {
        await InsertAsync(AuthAuditEvents.LoginSucceeded, true, Start);

        using (var anonymous = await Client.GetAsync(AuditRoute, Ct))
        {
            await AssertProblemAsync(anonymous, HttpStatusCode.Unauthorized, "http.401");
        }

        await SignInAsync(AllOtherAdminPermissions, AuthTestHarness.DefaultLocale);
        using (var forbidden = await Client.GetAsync(AuditRoute, Ct))
        {
            await AssertProblemAsync(forbidden, HttpStatusCode.Forbidden, "http.403");
        }

        await SignInAsync(AuthPermissions.AuditView);
        using var allowed = await Client.GetAsync(AuditRoute, Ct);
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await allowed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Filters_by_user_event_and_date()
    {
        await SignInAsync(AuthPermissions.AuditView);
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var session = Guid.NewGuid();
        var aliceLogin = await InsertAsync(AuthAuditEvents.LoginSucceeded, true, Start.AddDays(-3), alice, sessionId: session, ip: "203.0.113.7");
        var aliceFailure = await InsertAsync(AuthAuditEvents.LoginFailed, false, Start.AddDays(-2), alice, failureReason: "auth.invalid_credentials");
        var bobLogin = await InsertAsync(AuthAuditEvents.LoginSucceeded, true, Start.AddDays(-2).AddHours(1), bob);
        var aliceLogout = await InsertAsync(AuthAuditEvents.Logout, true, Start.AddDays(-1), alice, details: """{"reason":"user"}""");
        var anonymous = await InsertAsync(AuthAuditEvents.RegisterDuplicate, true, Start.AddHours(-12), attemptedIdentifier: "a****@example.com");

        (await IdsAsync($"?userId={alice}")).ShouldBe([aliceLogout, aliceFailure, aliceLogin]);
        (await IdsAsync($"?userId={bob}")).ShouldBe([bobLogin]);
        (await IdsAsync($"?eventType={AuthAuditEvents.LoginSucceeded}")).ShouldBe([bobLogin, aliceLogin]);
        (await IdsAsync("?succeeded=false")).ShouldBe([aliceFailure]);
        (await IdsAsync("?succeeded=true")).ShouldBe([anonymous, aliceLogout, bobLogin, aliceLogin]);
        (await IdsAsync($"?userId={alice}&eventType={AuthAuditEvents.LoginSucceeded}&succeeded=true")).ShouldBe([aliceLogin]);

        // Both ends are inclusive and compared as UTC instants, whatever offset the caller writes.
        (await IdsAsync($"?from={Stamp(Start.AddDays(-2))}")).ShouldBe([anonymous, aliceLogout, bobLogin, aliceFailure]);
        (await IdsAsync($"?to={Stamp(Start.AddDays(-2))}")).ShouldBe([aliceFailure, aliceLogin]);
        (await IdsAsync($"?from={Stamp(Start.AddDays(-2))}&to={Stamp(Start.AddDays(-2))}")).ShouldBe([aliceFailure]);
        (await IdsAsync($"?from={Stamp(Start.AddDays(-2).AddHours(1))}&to={Stamp(Start.AddDays(-1))}")).ShouldBe([aliceLogout, bobLogin]);
        (await IdsAsync($"?from={Stamp(Start.AddDays(-2).ToOffset(TimeSpan.FromHours(8)))}&to=2026-08-30T16:00:00%2B08:00")).ShouldBe([aliceFailure]);
        (await IdsAsync($"?userId={alice}&from={Stamp(Start.AddDays(-2))}")).ShouldBe([aliceLogout, aliceFailure]);
        (await IdsAsync($"?from={Stamp(Start.AddDays(1))}")).ShouldBeEmpty();

        // A date without an offset is UTC; a blank filter is no filter.
        (await IdsAsync("?from=2026-08-31T08:00:00&to=2026-08-31T08:00:00")).ShouldBe([aliceLogout]);
        (await IdsAsync("?from=&to=&eventType=")).Count.ShouldBe(5);

        // Totals follow the filters.
        var counted = await GetPageAsync($"?userId={alice}&includeTotalCount=true&pageSize=1");
        counted.GetProperty("totalCount").GetInt64().ShouldBe(3);
        counted.GetProperty("items").GetArrayLength().ShouldBe(1);

        // A cursor belongs to its filters.
        using (var mismatch = await Client.GetAsync($"{AuditRoute}?userId={bob}&pageSize=1&cursor={counted.GetProperty("nextCursor").GetString()}", Ct))
        {
            await AssertProblemAsync(mismatch, HttpStatusCode.BadRequest, "pagination.cursor_mismatch");
        }

        // Exactly these members: the identifier is the masked one the writer stored, and no secret is stored in the row at all.
        var login = (await GetPageAsync($"?userId={alice}&eventType={AuthAuditEvents.LoginSucceeded}")).GetProperty("items").EnumerateArray().Single();
        login.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["id", "occurredAt", "userId", "eventType", "succeeded", "failureReason", "attemptedIdentifier", "ipAddress", "userAgent", "sessionId", "traceId", "details"],
            ignoreOrder: true);
        login.GetProperty("id").GetInt64().ShouldBe(aliceLogin);
        login.GetProperty("occurredAt").GetDateTimeOffset().ShouldBe(Start.AddDays(-3));
        login.GetProperty("userId").GetGuid().ShouldBe(alice);
        login.GetProperty("succeeded").GetBoolean().ShouldBeTrue();
        login.GetProperty("sessionId").GetGuid().ShouldBe(session);
        login.GetProperty("ipAddress").GetString().ShouldBe("203.0.113.7");
        login.GetProperty("failureReason").ValueKind.ShouldBe(JsonValueKind.Null);
        var logout = (await GetPageAsync($"?eventType={AuthAuditEvents.Logout}")).GetProperty("items").EnumerateArray().Single();
        logout.GetProperty("details").GetString().ShouldBe("""{"reason":"user"}""");
        var duplicate = (await GetPageAsync($"?eventType={AuthAuditEvents.RegisterDuplicate}")).GetProperty("items").EnumerateArray().Single();
        duplicate.GetProperty("attemptedIdentifier").GetString().ShouldBe("a****@example.com");
        duplicate.GetProperty("userId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Invalid_filters_are_400()
    {
        await SignInAsync(AuthPermissions.AuditView);

        foreach (var query in new[]
        {
            "?eventType=auth.nothing",
            "?eventType=AUTH.LOGIN_SUCCEEDED",
            "?from=yesterday",
            "?to=2026-13-45",
            "?from=2026-09-02T00:00:00Z&to=2026-09-01T00:00:00Z",
            "?userId=not-a-guid",
            "?succeeded=maybe",
            "?pageSize=0",
            "?pageSize=101",
        })
        {
            using var response = await Client.GetAsync(AuditRoute + query, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, query);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json", query);
        }

        using var invalid = await Client.GetAsync($"{AuditRoute}?from=2026-09-02T00:00:00Z&to=2026-09-01T00:00:00Z", Ct);
        var body = await invalid.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe("validation.failed");
        body.GetProperty("errors").TryGetProperty("to", out _).ShouldBeTrue();

        using var badEvent = await Client.GetAsync($"{AuditRoute}?eventType=auth.nothing", Ct);
        (await badEvent.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").TryGetProperty("eventType", out _).ShouldBeTrue();

        foreach (var sort in new[] { "id", "eventType", "-userId" })
        {
            using var badSort = await Client.GetAsync($"{AuditRoute}?sort={sort}", Ct);
            await AssertProblemAsync(badSort, HttpStatusCode.BadRequest, "pagination.invalid_sort");
        }
    }

    [Fact]
    public async Task Pages_by_cursor_without_duplicates_when_rows_share_a_timestamp()
    {
        await SignInAsync(AuthPermissions.AuditView);
        var shared = Start;
        var inserted = new List<(long Id, DateTimeOffset At)>();
        inserted.Add((await InsertAsync(AuthAuditEvents.LoginFailed, false, shared.AddMinutes(-5)), shared.AddMinutes(-5)));
        inserted.Add((await InsertAsync(AuthAuditEvents.LoginFailed, false, shared.AddMinutes(-5)), shared.AddMinutes(-5)));
        for (var index = 0; index < 9; index++)
        {
            inserted.Add((await InsertAsync(AuthAuditEvents.LoginSucceeded, true, shared), shared));
        }

        inserted.Add((await InsertAsync(AuthAuditEvents.Logout, true, shared.AddMinutes(5)), shared.AddMinutes(5)));

        // The rows really do tie: three distinct instants for twelve rows.
        (await QueryAsync(context => context.Set<AuthAuditLog>().Select(entry => entry.OccurredAt).Distinct().CountAsync(Ct))).ShouldBe(3);
        var newestFirst = inserted.OrderByDescending(row => row.At).ThenByDescending(row => row.Id).Select(row => row.Id).ToList();

        // Forward, four at a time: every row once, in (occurredAt, id) descending order.
        var seen = new List<long>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await GetPageAsync($"?pageSize=4&includeTotalCount=true{(cursor is null ? string.Empty : "&cursor=" + cursor)}");
            page.GetProperty("totalCount").GetInt64().ShouldBe(12);
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt64()));
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null);

        pages.ShouldBe(3);
        seen.ShouldBe(newestFirst);
        seen.Distinct().Count().ShouldBe(12);

        // Backward from the last page with the previous cursors reaches the same rows.
        var last = await GetPageAsync("?pageSize=4&sort=occurredAt");
        var ascending = inserted.OrderBy(row => row.At).ThenBy(row => row.Id).Select(row => row.Id).ToList();
        last.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt64()).ShouldBe(ascending.Take(4));
        var walked = new List<long>(last.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt64()));
        var forward = last;
        while (forward.GetProperty("nextCursor").GetString() is { } next)
        {
            forward = await GetPageAsync($"?pageSize=4&sort=occurredAt&cursor={next}");
            walked.AddRange(forward.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt64()));
        }

        walked.ShouldBe(ascending);
        var back = await GetPageAsync($"?pageSize=4&sort=occurredAt&cursor={forward.GetProperty("previousCursor").GetString()}");
        back.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt64()).ShouldBe(ascending.Skip(4).Take(4));
        var backAgain = await GetPageAsync($"?pageSize=4&sort=occurredAt&cursor={back.GetProperty("previousCursor").GetString()}");
        backAgain.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt64()).ShouldBe(ascending.Take(4));
    }

    [Fact]
    public async Task Role_administration_appears_in_the_log_with_its_actor()
    {
        var actor = await SignInAsync(AuthPermissions.AuditView, AuthPermissions.RoleManage);
        using (var created = await Client.PostAsJsonAsync(RolesRoute, new { name = "Support", description = "x" }, Ct))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var entry = (await GetPageAsync($"?eventType={AuthAuditEvents.RoleCreated}")).GetProperty("items").EnumerateArray().Single();

        entry.GetProperty("succeeded").GetBoolean().ShouldBeTrue();
        entry.GetProperty("userId").ValueKind.ShouldBe(JsonValueKind.Null);
        entry.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
        using var details = JsonDocument.Parse(entry.GetProperty("details").GetString()!);
        details.RootElement.GetProperty("actorId").GetGuid().ShouldBe(actor.UserId);
        details.RootElement.GetProperty("name").GetString().ShouldBe("Support");
    }

    private static string Stamp(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("O", CultureInfo.InvariantCulture));

    private async Task<JsonElement> GetPageAsync(string query)
    {
        using var response = await Client.GetAsync(AuditRoute + query, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, query);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<List<long>> IdsAsync(string query)
        => [.. (await GetPageAsync(query + (query.Contains("pageSize", StringComparison.Ordinal) ? string.Empty : (query.Length == 0 ? "?" : "&") + "pageSize=100")))
            .GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt64())];

    private async Task<long> InsertAsync(
        string eventType,
        bool succeeded,
        DateTimeOffset at,
        Guid? userId = null,
        string? failureReason = null,
        string? attemptedIdentifier = null,
        Guid? sessionId = null,
        string? details = null,
        string? ip = null)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var entry = AuthAuditLog.Create(eventType, succeeded, at, userId, failureReason, attemptedIdentifier, sessionId, details);
        entry.SetClientInfo(ip, userAgent: null, traceId: null);
        context.Add(entry);
        await context.SaveChangesAsync(Ct);
        return entry.Id;
    }
}
