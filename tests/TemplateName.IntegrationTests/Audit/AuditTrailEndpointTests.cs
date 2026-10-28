using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.IntegrationTests.Auth;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Audit.Application;
using TemplateName.Modules.Audit.Domain.Trails;
using TemplateName.Modules.Audit.Infrastructure.Persistence;
using TemplateName.Modules.Auth.Application;
using TemplateName.SharedKernel.Auditing;

namespace TemplateName.IntegrationTests.Audit;

/// <summary>
/// <c>GET /api/v1/admin/audit/trails</c> through the running host. The rows are inserted straight into <c>audit.AuditTrails</c> through
/// <c>AuditDbContext</c> with chosen timestamps; the list is read through Dapper, so its paging is proved against SQL Server, ties
/// included.
/// </summary>
public sealed class AuditTrailEndpointTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string TrailsRoute = "/api/v1/admin/audit/trails";

    private static readonly DateTimeOffset Start = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Requires_the_trails_view_permission()
    {
        await InsertAsync("LeaveRequest", "1", EntityChangeAction.Created, Start);

        using (var anonymous = await Client.GetAsync(TrailsRoute, Ct))
        {
            await AdminTestBase.AssertProblemAsync(anonymous, HttpStatusCode.Unauthorized, "http.401");
        }

        await SignInAsync(AuthPermissions.UserView, AuthPermissions.AuditView);
        using (var forbidden = await Client.GetAsync(TrailsRoute, Ct))
        {
            await AdminTestBase.AssertProblemAsync(forbidden, HttpStatusCode.Forbidden, "http.403");
        }

        await SignInAsync(AuditPermissions.TrailsView);
        using var allowed = await Client.GetAsync(TrailsRoute, Ct);
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await allowed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Each_filter_narrows_the_list_alone_and_together()
    {
        await SignInAsync(AuditPermissions.TrailsView);
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var firstSave = Guid.NewGuid();
        var secondSave = Guid.NewGuid();
        var aliceCreated = await InsertAsync("LeaveRequest", "7", EntityChangeAction.Created, Start.AddDays(-3), alice, firstSave, "sample");
        var aliceUpdated = await InsertAsync("LeaveRequest", "7", EntityChangeAction.Updated, Start.AddDays(-2), alice, secondSave, "sample");
        var bobUpdated = await InsertAsync("LeaveRequest", "8", EntityChangeAction.Updated, Start.AddDays(-2).AddHours(1), bob, Guid.NewGuid(), "sample");
        var aliceRole = await InsertAsync("Role", "7", EntityChangeAction.SoftDeleted, Start.AddDays(-1), alice, secondSave, "auth");
        var system = await InsertAsync("Role", "9", EntityChangeAction.Restored, Start.AddHours(-12), userId: null, Guid.NewGuid(), "auth");

        (await IdsAsync(string.Empty)).ShouldBe([system, aliceRole, bobUpdated, aliceUpdated, aliceCreated]);
        (await IdsAsync("?entityType=LeaveRequest")).ShouldBe([bobUpdated, aliceUpdated, aliceCreated]);
        (await IdsAsync("?entityType=Role")).ShouldBe([system, aliceRole]);
        (await IdsAsync("?entityId=7")).ShouldBe([aliceRole, aliceUpdated, aliceCreated]);
        (await IdsAsync($"?userId={alice}")).ShouldBe([aliceRole, aliceUpdated, aliceCreated]);
        (await IdsAsync($"?userId={bob}")).ShouldBe([bobUpdated]);
        (await IdsAsync($"?changeSetId={secondSave}")).ShouldBe([aliceRole, aliceUpdated]);
        (await IdsAsync("?module=sample")).ShouldBe([bobUpdated, aliceUpdated, aliceCreated]);
        (await IdsAsync("?module=auth")).ShouldBe([system, aliceRole]);
        (await IdsAsync("?action=Updated")).ShouldBe([bobUpdated, aliceUpdated]);
        (await IdsAsync("?action=SoftDeleted")).ShouldBe([aliceRole]);
        (await IdsAsync("?action=Deleted")).ShouldBeEmpty();

        // Both ends are inclusive and compared as UTC instants, whatever offset the caller writes.
        (await IdsAsync($"?from={Stamp(Start.AddDays(-2))}")).ShouldBe([system, aliceRole, bobUpdated, aliceUpdated]);
        (await IdsAsync($"?to={Stamp(Start.AddDays(-2))}")).ShouldBe([aliceUpdated, aliceCreated]);
        (await IdsAsync($"?from={Stamp(Start.AddDays(-2))}&to={Stamp(Start.AddDays(-2))}")).ShouldBe([aliceUpdated]);
        (await IdsAsync($"?from={Stamp(Start.AddDays(-2).ToOffset(TimeSpan.FromHours(8)))}&to=2026-08-30T16:00:00%2B08:00")).ShouldBe([aliceUpdated]);
        (await IdsAsync($"?from={Stamp(Start.AddDays(1))}")).ShouldBeEmpty();

        // A date without an offset is UTC; a blank filter is no filter.
        (await IdsAsync("?from=2026-08-31T08:00:00&to=2026-08-31T08:00:00")).ShouldBe([aliceRole]);
        (await IdsAsync("?from=&to=&action=&entityType=&entityId=&module=")).Count.ShouldBe(5);

        // The filters combine with AND.
        (await IdsAsync($"?entityType=LeaveRequest&entityId=7&userId={alice}")).ShouldBe([aliceUpdated, aliceCreated]);
        (await IdsAsync($"?entityType=Role&entityId=7&userId={alice}&module=auth&action=SoftDeleted&changeSetId={secondSave}")).ShouldBe([aliceRole]);
        (await IdsAsync($"?userId={alice}&from={Stamp(Start.AddDays(-2))}")).ShouldBe([aliceRole, aliceUpdated]);
    }

    [Fact]
    public async Task A_combination_that_matches_nothing_is_an_empty_page()
    {
        await SignInAsync(AuditPermissions.TrailsView);
        var alice = Guid.NewGuid();
        await InsertAsync("LeaveRequest", "7", EntityChangeAction.Created, Start, alice);
        await InsertAsync("Role", "7", EntityChangeAction.Created, Start, Guid.NewGuid());

        // Each filter matches a row; together they match none.
        var page = await GetPageAsync($"?entityType=Role&entityId=7&userId={alice}&includeTotalCount=true");

        page.GetProperty("items").GetArrayLength().ShouldBe(0);
        page.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        page.GetProperty("previousCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        page.GetProperty("totalCount").GetInt64().ShouldBe(0);
        (await IdsAsync($"?entityType=Role&entityId=7&userId={Guid.NewGuid()}")).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_item_has_exactly_the_documented_members_and_changes_is_a_json_object()
    {
        await SignInAsync(AuditPermissions.TrailsView);
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var changeSetId = Guid.NewGuid();
        var id = await InsertAsync(
            "LeaveRequest",
            "42",
            EntityChangeAction.Updated,
            Start,
            userId,
            changeSetId,
            "sample",
            """{"Status":{"old":"Pending","new":"Approved"},"Days":{"old":2,"new":3},"Note":{"old":null,"new":"ok"}}""",
            sessionId,
            "4bf92f3577b34da6a3ce929d0e0e4736");
        var anonymous = await InsertAsync("Role", "9", EntityChangeAction.Created, Start.AddMinutes(-1), changes: "{}");

        var items = (await GetPageAsync(string.Empty)).GetProperty("items").EnumerateArray().ToList();
        var item = items[0];

        item.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["id", "changeSetId", "occurredAt", "userId", "sessionId", "module", "entityType", "entityId", "action", "changes", "traceId"],
            ignoreOrder: true);
        item.GetProperty("id").GetInt64().ShouldBe(id);
        item.GetProperty("changeSetId").GetGuid().ShouldBe(changeSetId);
        item.GetProperty("occurredAt").GetDateTimeOffset().ShouldBe(Start);
        item.GetProperty("userId").GetGuid().ShouldBe(userId);
        item.GetProperty("sessionId").GetGuid().ShouldBe(sessionId);
        item.GetProperty("module").GetString().ShouldBe("sample");
        item.GetProperty("entityType").GetString().ShouldBe("LeaveRequest");
        item.GetProperty("entityId").GetString().ShouldBe("42");
        item.GetProperty("action").GetString().ShouldBe("Updated");
        item.GetProperty("traceId").GetString().ShouldBe("4bf92f3577b34da6a3ce929d0e0e4736");

        // A JSON object, not a string holding JSON text.
        var changes = item.GetProperty("changes");
        changes.ValueKind.ShouldBe(JsonValueKind.Object);
        changes.GetProperty("Status").GetProperty("old").GetString().ShouldBe("Pending");
        changes.GetProperty("Status").GetProperty("new").GetString().ShouldBe("Approved");
        changes.GetProperty("Days").GetProperty("new").GetInt32().ShouldBe(3);
        changes.GetProperty("Note").GetProperty("old").ValueKind.ShouldBe(JsonValueKind.Null);

        var other = items[1];
        other.GetProperty("id").GetInt64().ShouldBe(anonymous);
        other.GetProperty("changes").ValueKind.ShouldBe(JsonValueKind.Object);
        other.GetProperty("userId").ValueKind.ShouldBe(JsonValueKind.Null);
        other.GetProperty("sessionId").ValueKind.ShouldBe(JsonValueKind.Null);
        other.GetProperty("traceId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_default_order_is_newest_first_and_sort_occurred_at_reverses_it()
    {
        await SignInAsync(AuditPermissions.TrailsView);
        var oldest = await InsertAsync("LeaveRequest", "1", EntityChangeAction.Created, Start.AddHours(-2));
        var newest = await InsertAsync("LeaveRequest", "1", EntityChangeAction.Updated, Start);
        var middle = await InsertAsync("LeaveRequest", "1", EntityChangeAction.Updated, Start.AddHours(-1));

        (await IdsAsync(string.Empty)).ShouldBe([newest, middle, oldest]);
        (await IdsAsync("?sort=-occurredAt")).ShouldBe([newest, middle, oldest]);
        (await IdsAsync("?sort=occurredAt")).ShouldBe([oldest, middle, newest]);
    }

    [Fact]
    public async Task Rows_with_the_same_occurred_at_keep_the_id_order_across_pages_in_both_directions()
    {
        await SignInAsync(AuditPermissions.TrailsView);
        var inserted = new List<(long Id, DateTimeOffset At)>
        {
            (await InsertAsync("LeaveRequest", "1", EntityChangeAction.Created, Start.AddMinutes(-5)), Start.AddMinutes(-5)),
            (await InsertAsync("LeaveRequest", "2", EntityChangeAction.Created, Start.AddMinutes(-5)), Start.AddMinutes(-5)),
        };
        for (var index = 0; index < 9; index++)
        {
            inserted.Add((await InsertAsync("LeaveRequest", index.ToString(CultureInfo.InvariantCulture), EntityChangeAction.Updated, Start), Start));
        }

        inserted.Add((await InsertAsync("LeaveRequest", "3", EntityChangeAction.Deleted, Start.AddMinutes(5)), Start.AddMinutes(5)));

        // The rows really do tie: three distinct instants for twelve rows.
        (await CountDistinctInstantsAsync()).ShouldBe(3);
        var newestFirst = inserted.OrderByDescending(row => row.At).ThenByDescending(row => row.Id).Select(row => row.Id).ToList();
        var ascending = inserted.OrderBy(row => row.At).ThenBy(row => row.Id).Select(row => row.Id).ToList();

        // Forward, four at a time: every row once, in (occurredAt, id) descending order; the total counts them all.
        var seen = new List<long>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await GetPageAsync($"?pageSize=4&includeTotalCount=true{(cursor is null ? string.Empty : "&cursor=" + cursor)}");
            page.GetProperty("totalCount").GetInt64().ShouldBe(12);
            seen.AddRange(IdsOf(page));
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null);

        pages.ShouldBe(3);
        seen.ShouldBe(newestFirst);

        // Ascending forward, then backward with the previous cursors: the same pages again.
        var first = await GetPageAsync("?pageSize=4&sort=occurredAt");
        IdsOf(first).ShouldBe(ascending.Take(4));
        first.GetProperty("previousCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        var second = await GetPageAsync($"?pageSize=4&sort=occurredAt&cursor={first.GetProperty("nextCursor").GetString()}");
        IdsOf(second).ShouldBe(ascending.Skip(4).Take(4));
        var third = await GetPageAsync($"?pageSize=4&sort=occurredAt&cursor={second.GetProperty("nextCursor").GetString()}");
        IdsOf(third).ShouldBe(ascending.Skip(8).Take(4));
        third.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);

        var backToSecond = await GetPageAsync($"?pageSize=4&sort=occurredAt&cursor={third.GetProperty("previousCursor").GetString()}");
        IdsOf(backToSecond).ShouldBe(IdsOf(second));
        var backToFirst = await GetPageAsync($"?pageSize=4&sort=occurredAt&cursor={backToSecond.GetProperty("previousCursor").GetString()}");
        IdsOf(backToFirst).ShouldBe(IdsOf(first));
        backToFirst.GetProperty("previousCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_total_count_follows_the_filters_and_is_left_out_unless_asked_for()
    {
        await SignInAsync(AuditPermissions.TrailsView);
        for (var index = 0; index < 5; index++)
        {
            await InsertAsync("LeaveRequest", "1", EntityChangeAction.Updated, Start.AddMinutes(index));
        }

        await InsertAsync("Role", "1", EntityChangeAction.Created, Start);

        var counted = await GetPageAsync("?entityType=LeaveRequest&pageSize=2&includeTotalCount=true");
        counted.GetProperty("totalCount").GetInt64().ShouldBe(5);
        counted.GetProperty("items").GetArrayLength().ShouldBe(2);
        (await GetPageAsync("?includeTotalCount=true")).GetProperty("totalCount").GetInt64().ShouldBe(6);
        (await GetPageAsync("?pageSize=2")).TryGetProperty("totalCount", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_cursor_replayed_with_another_filter_is_refused()
    {
        await SignInAsync(AuditPermissions.TrailsView);
        for (var index = 0; index < 3; index++)
        {
            await InsertAsync("LeaveRequest", "1", EntityChangeAction.Updated, Start.AddMinutes(index));
        }

        var cursor = (await GetPageAsync("?entityType=LeaveRequest&pageSize=1")).GetProperty("nextCursor").GetString();

        using (var sameFilter = await Client.GetAsync($"{TrailsRoute}?entityType=LeaveRequest&pageSize=1&cursor={cursor}", Ct))
        {
            sameFilter.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        foreach (var other in new[] { "?entityType=Role", "?entityType=LeaveRequest&entityId=1", "?entityType=LeaveRequest&action=Updated", string.Empty })
        {
            using var mismatch = await Client.GetAsync($"{TrailsRoute}{other}{(other.Length == 0 ? "?" : "&")}pageSize=1&cursor={cursor}", Ct);
            await AdminTestBase.AssertProblemAsync(mismatch, HttpStatusCode.BadRequest, "pagination.cursor_mismatch");
        }
    }

    [Fact]
    public async Task Invalid_input_is_a_400_with_field_errors()
    {
        await SignInAsync(AuditPermissions.TrailsView);

        foreach (var query in new[]
        {
            "?action=created",
            "?action=Nothing",
            "?from=yesterday",
            "?to=2026-13-45",
            "?from=2026-09-02T00:00:00Z&to=2026-09-01T00:00:00Z",
            "?userId=not-a-guid",
            "?changeSetId=not-a-guid",
            "?pageSize=0",
            "?pageSize=101",
        })
        {
            using var response = await Client.GetAsync(TrailsRoute + query, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, query);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json", query);
        }

        using var reversed = await Client.GetAsync($"{TrailsRoute}?from=2026-09-02T00:00:00Z&to=2026-09-01T00:00:00Z", Ct);
        var body = await reversed.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe("validation.failed");
        body.GetProperty("errors").GetProperty("to")[0].GetString().ShouldBe("'To' must not be earlier than 'From'.");

        using var badAction = await Client.GetAsync($"{TrailsRoute}?action=created", Ct);
        var actionBody = await badAction.Content.ReadFromJsonAsync<JsonElement>(Ct);
        actionBody.GetProperty("code").GetString().ShouldBe("validation.failed");
        actionBody.GetProperty("errors").TryGetProperty("action", out _).ShouldBeTrue();

        foreach (var sort in new[] { "id", "entityType", "-userId" })
        {
            using var badSort = await Client.GetAsync($"{TrailsRoute}?sort={sort}", Ct);
            await AdminTestBase.AssertProblemAsync(badSort, HttpStatusCode.BadRequest, "pagination.invalid_sort");
        }
    }

    [Fact]
    public async Task The_unfiltered_list_and_the_history_of_one_record_are_served_by_their_indexes_without_a_sort()
    {
        await SignInAsync(AuditPermissions.TrailsView);
        await SeedManyAsync(rows: 60_000);

        // Compile the statements fresh, as the host runs them, then read the plan SQL Server cached for each.
        await ClearPlanCacheAsync();
        using (var unfiltered = await Client.GetAsync($"{TrailsRoute}?pageSize=20", Ct))
        {
            unfiltered.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var history = await Client.GetAsync($"{TrailsRoute}?entityType=LeaveRequest&entityId=18&pageSize=20", Ct))
        {
            history.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await using var scope = Factory.Services.CreateAsyncScope();
        var connection = scope.ServiceProvider.GetRequiredService<AuditDbContext>().Database.GetDbConnection();

        var unfilteredPlan = await SqlExecutionPlan.OfCachedStatementAsync(connection, "WHERE 1 = 1 AND 1 = 1", Ct);
        SqlExecutionPlan.PhysicalOperatorsOf(unfilteredPlan).ShouldNotContain(operation => operation.Contains("Sort", StringComparison.Ordinal));
        unfilteredPlan.ShouldContain("IX_AuditTrails_OccurredAt_Id");

        var historyPlan = await SqlExecutionPlan.OfCachedStatementAsync(connection, "WHERE 1 = 1 AND EntityType = @EntityType AND EntityId = @EntityId AND 1 = 1", Ct);
        SqlExecutionPlan.PhysicalOperatorsOf(historyPlan).ShouldNotContain(operation => operation.Contains("Sort", StringComparison.Ordinal));
        SqlExecutionPlan.PhysicalOperatorsOf(historyPlan).ShouldContain("Index Seek");
        historyPlan.ShouldContain("IX_AuditTrails_EntityType_EntityId_OccurredAt_Id");
    }

    private static string Stamp(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("O", CultureInfo.InvariantCulture));

    private static List<long> IdsOf(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt64())];

    private async Task<JsonElement> GetPageAsync(string query)
    {
        using var response = await Client.GetAsync(TrailsRoute + query, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, query);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<List<long>> IdsAsync(string query)
        => IdsOf(await GetPageAsync(query + (query.Contains("pageSize", StringComparison.Ordinal) ? string.Empty : (query.Length == 0 ? "?" : "&") + "pageSize=100")));

    private async Task<int> CountDistinctInstantsAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Set<AuditTrail>().Select(trail => trail.OccurredAt).Distinct().CountAsync(Ct);
    }

    private async Task<long> InsertAsync(
        string entityType,
        string entityId,
        EntityChangeAction action,
        DateTimeOffset at,
        Guid? userId = null,
        Guid? changeSetId = null,
        string module = "sample",
        string changes = """{"Name":{"new":"x"}}""",
        Guid? sessionId = null,
        string? traceId = null)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var trail = AuditTrail.Create(changeSetId ?? Guid.NewGuid(), at, userId, sessionId, module, new EntityChange(entityType, entityId, action, changes), traceId);
        context.Add(trail);
        await context.SaveChangesAsync(Ct);
        return trail.Id;
    }

    /// <summary>Inserts <paramref name="rows"/> rows spread over many records, people and minutes, so the optimizer has real statistics to choose with.</summary>
    private async Task SeedManyAsync(int rows)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO [audit].[AuditTrails] ([ChangeSetId], [OccurredAt], [UserId], [SessionId], [Module], [EntityType], [EntityId], [Action], [Changes], [TraceId])
            SELECT NEWID(), DATEADD(minute, -CAST(n AS int), {1}), NEWID(), NULL, 'sample', CASE WHEN n % 3 = 0 THEN 'LeaveRequest' ELSE 'Role' END,
                CAST(n % 500 AS nvarchar(200)), 'Updated', '{{"Name":{{"new":"x"}}}}', NULL
            FROM (SELECT TOP ({0}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b) AS numbers;
            """,
            [rows, Start.UtcDateTime],
            Ct);
    }

    private async Task ClearPlanCacheAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Database.ExecuteSqlRawAsync("ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE", Ct);
    }
}
