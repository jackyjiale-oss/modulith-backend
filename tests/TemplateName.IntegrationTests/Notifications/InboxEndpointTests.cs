using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Notifications.Domain.InApp;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Notifications;

/// <summary>
/// The in-app inbox routes under <c>/api/v1/notifications/inbox</c> through the running host. The list and the unread count are read
/// through Dapper, which bypasses the EF Core model, so these tests prove their SQL keeps to the caller's own rows, pages by
/// <c>(CreatedAt, Id)</c> without skips or repeats and binds its cursor to the filter.
/// </summary>
public sealed class InboxEndpointTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string InboxRoute = "/api/v1/notifications/inbox";
    private const string UnreadCountRoute = "/api/v1/notifications/inbox/unread-count";
    private const string ReadAllRoute = "/api/v1/notifications/inbox/read-all";
    private const string TypeCode = "auth.password_changed";

    private DateTimeOffset Now => Factory.Time.GetUtcNow();

    [Fact]
    public async Task List_shows_only_my_notifications_newest_first()
    {
        var alice = await SignInAsync();
        var bob = Guid.NewGuid();
        var oldest = await InsertAsync(alice.UserId, Now.AddMinutes(-3), title: "Oldest");
        var middle = await InsertAsync(alice.UserId, Now.AddMinutes(-2), title: "Middle");
        var newest = await InsertAsync(alice.UserId, Now.AddMinutes(-1), title: "Newest", readAt: Now);
        await InsertAsync(bob, Now.AddMinutes(-1), title: "Bob's");

        var page = await GetPageAsync("?includeTotalCount=true");

        page.Ids.ShouldBe([newest, middle, oldest]);
        page.Body.GetProperty("pageSize").GetInt32().ShouldBe(20);
        page.Body.GetProperty("totalCount").GetInt64().ShouldBe(3);
        page.NextCursor.ShouldBeNull();
        page.PreviousCursor.ShouldBeNull();

        // Exactly these members, in the camelCase the rest of the API uses.
        var first = page.Items[0];
        first.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["id", "typeCode", "category", "title", "body", "createdAt", "readAt"],
            ignoreOrder: true);
        first.GetProperty("typeCode").GetString().ShouldBe(TypeCode);
        first.GetProperty("category").GetString().ShouldBe("Security");
        first.GetProperty("title").GetString().ShouldBe("Newest");
        first.GetProperty("body").GetString().ShouldBe("Body of Newest");
        first.GetProperty("createdAt").GetString().ShouldBe(Now.AddMinutes(-1).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        first.GetProperty("readAt").GetDateTimeOffset().ShouldBe(Now);
        page.Items[1].GetProperty("readAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_owner_is_the_token_user_never_a_value_of_the_request()
    {
        var alice = await SignInAsync();
        var bob = Guid.NewGuid();
        await InsertAsync(alice.UserId, Now, title: "Mine");
        var othersId = await InsertAsync(bob, Now, title: "Bob's");

        // A userId in the query string is not a parameter of the route.
        var page = await GetPageAsync($"?userId={bob}");

        page.Items.Select(item => item.GetProperty("title").GetString()).ShouldBe(["Mine"]);
        page.Ids.ShouldNotContain(othersId);
    }

    [Fact]
    public async Task Unread_only_filter_and_cursor_mismatch()
    {
        var alice = await SignInAsync();
        var rows = new List<(Guid Id, bool Read)>();
        for (var index = 0; index < 6; index++)
        {
            var read = index % 2 == 0;
            rows.Add((await InsertAsync(alice.UserId, Now.AddMinutes(index), readAt: read ? Now : null), read));
        }

        var unread = rows.Where(row => !row.Read).Select(row => row.Id).Reverse().ToList();

        var filtered = await GetPageAsync("?unreadOnly=true&includeTotalCount=true");
        filtered.Ids.ShouldBe(unread);
        filtered.Body.GetProperty("totalCount").GetInt64().ShouldBe(3);
        (await GetPageAsync("?unreadOnly=false&includeTotalCount=true")).Body.GetProperty("totalCount").GetInt64().ShouldBe(6);

        // A cursor is bound to the filter it was made with, in both directions.
        var unfilteredFirst = await GetPageAsync("?pageSize=2");
        unfilteredFirst.NextCursor.ShouldNotBeNull();
        (await GetProblemAsync($"?unreadOnly=true&pageSize=2&cursor={Uri.EscapeDataString(unfilteredFirst.NextCursor)}"))
            .GetProperty("code").GetString().ShouldBe("pagination.cursor_mismatch");

        var filteredFirst = await GetPageAsync("?unreadOnly=true&pageSize=2");
        filteredFirst.NextCursor.ShouldNotBeNull();
        (await GetProblemAsync($"?pageSize=2&cursor={Uri.EscapeDataString(filteredFirst.NextCursor)}"))
            .GetProperty("code").GetString().ShouldBe("pagination.cursor_mismatch");

        // The same filter continues the walk.
        var filteredSecond = await GetPageAsync($"?unreadOnly=true&pageSize=2&cursor={Uri.EscapeDataString(filteredFirst.NextCursor)}");
        filteredFirst.Ids.Concat(filteredSecond.Ids).ShouldBe(unread);
    }

    [Fact]
    public async Task Invalid_sort_and_page_size_are_rejected()
    {
        await SignInAsync();

        (await GetProblemAsync("?sort=title")).GetProperty("code").GetString().ShouldBe("pagination.invalid_sort");
        (await GetProblemAsync("?sort=id")).GetProperty("code").GetString().ShouldBe("pagination.invalid_sort");
        (await GetProblemAsync("?cursor=abc")).GetProperty("code").GetString().ShouldBe("pagination.invalid_cursor");
        foreach (var pageSize in new[] { 0, 101 })
        {
            var body = await GetProblemAsync($"?pageSize={pageSize}");
            body.GetProperty("code").GetString().ShouldBe("validation.failed");
            body.GetProperty("errors").TryGetProperty("pageSize", out _).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Paging_with_equal_timestamps_visits_every_row_once()
    {
        var alice = await SignInAsync();

        // 45 rows on three instants, 15 each: the id has to break every tie.
        var all = new List<Guid>();
        foreach (var instant in new[] { Now.AddMinutes(-2), Now.AddMinutes(-1), Now })
        {
            all.AddRange(await InsertManyAsync(alice.UserId, 15, instant));
        }

        // The order SQL Server itself gives: createdAt descending, the id descending within a tie (its Guid order is not .NET's).
        var expected = await QueryAsync(context => context.Set<InAppNotification>()
            .Where(row => row.UserId == alice.UserId)
            .OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id)
            .Select(row => row.Id).ToListAsync(Ct));

        var forward = await WalkForwardAsync("?pageSize=7");

        var forwardIds = forward.SelectMany(page => page.Ids).ToList();
        forward.Count.ShouldBe(7);
        forwardIds.ShouldBe(expected);
        forwardIds.ShouldBeUnique();
        forwardIds.ShouldBe(all, ignoreOrder: true);
        forward[0].PreviousCursor.ShouldBeNull();
        forward[^1].NextCursor.ShouldBeNull();
        forward[^1].Ids.Count.ShouldBe(3);

        // The same rows walking back from the last page, page for page.
        var backward = new List<Page>();
        var cursor = forward[^1].PreviousCursor;
        while (cursor is not null)
        {
            backward.Count.ShouldBeLessThan(20, "the walk does not end");
            var page = await GetPageAsync($"?pageSize=7&cursor={Uri.EscapeDataString(cursor)}");
            backward.Add(page);
            cursor = page.PreviousCursor;
        }

        backward.Select(page => page.Ids).ToList().ShouldBe(forward.SkipLast(1).Reverse().Select(page => page.Ids).ToList());
        backward[^1].NextCursor.ShouldNotBeNull();
    }

    [Fact]
    public async Task Rows_inserted_between_pages_cause_no_duplicates()
    {
        var alice = await SignInAsync();
        var seeded = new List<Guid>();
        for (var index = 0; index < 25; index++)
        {
            seeded.Add(await InsertAsync(alice.UserId, Now.AddSeconds(index)));
        }

        var newestFirst = seeded.AsEnumerable().Reverse().ToList();
        var page1 = await GetPageAsync("?pageSize=10");
        page1.Ids.ShouldBe(newestFirst.Take(10));

        // New notifications arrive after the client read the first page.
        var arrived = new List<Guid>
        {
            await InsertAsync(alice.UserId, Now.AddMinutes(5)),
            await InsertAsync(alice.UserId, Now.AddMinutes(6)),
        };

        var page2 = await GetPageAsync($"?pageSize=10&cursor={Uri.EscapeDataString(page1.NextCursor!)}");
        var page3 = await GetPageAsync($"?pageSize=10&cursor={Uri.EscapeDataString(page2.NextCursor!)}");

        page2.Ids.ShouldBe(newestFirst.Skip(10).Take(10));
        page3.Ids.ShouldBe(newestFirst.Skip(20));
        page1.Ids.Concat(page2.Ids).Concat(page3.Ids).ShouldBeUnique();
        page2.Ids.Concat(page3.Ids).ShouldAllBe(id => !arrived.Contains(id));

        // Walking back from the second page still reaches the first page's rows (it does not shift by the new ones), and one page
        // further back is where the new rows are.
        var back = await GetPageAsync($"?pageSize=10&cursor={Uri.EscapeDataString(page2.PreviousCursor!)}");
        back.Ids.ShouldBe(page1.Ids);
        back.PreviousCursor.ShouldNotBeNull();
        (await GetPageAsync($"?pageSize=10&cursor={Uri.EscapeDataString(back.PreviousCursor)}")).Ids.ShouldBe(arrived.AsEnumerable().Reverse());
    }

    [Fact]
    public async Task Unread_count_counts_only_my_unread_rows()
    {
        var alice = await SignInAsync();
        await InsertAsync(alice.UserId, Now);
        await InsertAsync(alice.UserId, Now);
        await InsertAsync(alice.UserId, Now, readAt: Now);
        await InsertAsync(Guid.NewGuid(), Now);

        (await GetUnreadCountAsync()).ShouldBe(new UnreadCount(2, IsCapped: false));
    }

    [Fact]
    public async Task Unread_count_is_capped_at_1000()
    {
        var alice = await SignInAsync();
        var bob = Guid.NewGuid();
        await InsertManyAsync(alice.UserId, 1005, Now);
        await InsertManyAsync(alice.UserId, 5, Now, readAt: Now);
        await InsertManyAsync(bob, 5, Now);

        var count = await GetUnreadCountAsync();

        count.Count.ShouldBe(1000);
        count.IsCapped.ShouldBeTrue();
    }

    [Fact]
    public async Task Exactly_1000_unread_rows_are_reported_as_capped()
    {
        // The count cannot tell 1000 from "1000 or more", so it says capped from 1000 on (documented).
        var alice = await SignInAsync();
        await InsertManyAsync(alice.UserId, 999, Now);
        (await GetUnreadCountAsync()).ShouldBe(new UnreadCount(999, IsCapped: false));

        await InsertManyAsync(alice.UserId, 1, Now);

        (await GetUnreadCountAsync()).ShouldBe(new UnreadCount(1000, IsCapped: true));
    }

    [Fact]
    public async Task Mark_read_is_idempotent_and_404_for_others()
    {
        var alice = await SignInAsync();
        var bob = Guid.NewGuid();
        var mine = await InsertAsync(alice.UserId, Now);
        var others = await InsertAsync(bob, Now);
        var readAt = Now;

        using (var first = await PostAsync($"{InboxRoute}/{mine}/read"))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            (await first.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty();
        }

        (await ReadAtOfAsync(mine)).ShouldBe(readAt.UtcDateTime);

        // A second call is not an error and keeps the first time.
        Factory.Time.Advance(TimeSpan.FromMinutes(10));
        using (var second = await PostAsync($"{InboxRoute}/{mine}/read"))
        {
            second.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await ReadAtOfAsync(mine)).ShouldBe(readAt.UtcDateTime);
        (await GetUnreadCountAsync()).Count.ShouldBe(0);

        // Another user's row and an unknown id answer alike (never 403), so ids cannot be probed.
        var unknown = Guid.NewGuid();
        using var other = await PostAsync($"{InboxRoute}/{others}/read");
        using var missing = await PostAsync($"{InboxRoute}/{unknown}/read");

        other.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var otherBody = await other.Content.ReadAsStringAsync(Ct);
        var missingBody = await missing.Content.ReadAsStringAsync(Ct);
        var otherJson = JsonDocument.Parse(otherBody).RootElement;
        otherJson.GetProperty("code").GetString().ShouldBe("notifications.notification_not_found");
        otherJson.GetProperty("params").GetProperty("id").GetGuid().ShouldBe(others);
        Normalize(otherBody, others).ShouldBe(Normalize(missingBody, unknown));

        // Bob's row was not touched.
        (await ReadAtOfAsync(others)).ShouldBeNull();
    }

    [Fact]
    public async Task Read_all_marks_only_my_rows()
    {
        var alice = await SignInAsync();
        var bob = Guid.NewGuid();
        var earlier = Now.AddHours(-2);
        var unread = new List<Guid>
        {
            await InsertAsync(alice.UserId, Now.AddMinutes(-30)),
            await InsertAsync(alice.UserId, Now.AddMinutes(-20)),
            await InsertAsync(alice.UserId, Now),
        };
        var alreadyRead = await InsertAsync(alice.UserId, Now.AddHours(-1), readAt: earlier);
        var later = await InsertAsync(alice.UserId, Now.AddSeconds(1));
        var others = new List<Guid> { await InsertAsync(bob, Now.AddMinutes(-30)), await InsertAsync(bob, Now) };
        var markedAt = Now;

        using (var response = await PostAsync(ReadAllRoute))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty();
        }

        foreach (var id in unread)
        {
            (await ReadAtOfAsync(id)).ShouldBe(markedAt.UtcDateTime);
        }

        // Read before stays as it was; created after the server's now stays unread; Bob's rows are not Alice's to mark.
        (await ReadAtOfAsync(alreadyRead)).ShouldBe(earlier.UtcDateTime);
        (await ReadAtOfAsync(later)).ShouldBeNull();
        foreach (var id in others)
        {
            (await ReadAtOfAsync(id)).ShouldBeNull();
        }

        (await GetUnreadCountAsync()).Count.ShouldBe(1);

        // Once the clock has passed the last row, the next call covers it and leaves the earlier marks as they were.
        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        using (var again = await PostAsync(ReadAllRoute))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await ReadAtOfAsync(later)).ShouldBe(Now.UtcDateTime);
        (await ReadAtOfAsync(unread[0])).ShouldBe(markedAt.UtcDateTime);
        (await GetUnreadCountAsync()).Count.ShouldBe(0);
    }

    [Fact]
    public async Task Every_route_requires_a_token()
    {
        // No SignInAsync: the client is anonymous.
        var id = Guid.NewGuid();
        using var list = await Client.GetAsync(InboxRoute, Ct);
        using var count = await Client.GetAsync(UnreadCountRoute, Ct);
        using var markRead = await Client.PostAsync($"{InboxRoute}/{id}/read", content: null, Ct);
        using var readAll = await Client.PostAsync(ReadAllRoute, content: null, Ct);

        list.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        count.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        markRead.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        readAll.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // The trace id and the notification id are the only parts that differ between two otherwise identical problem bodies.
    private static string Normalize(string body, Guid id)
    {
        var node = JsonNode.Parse(body)!.AsObject();
        node.Remove("traceId");
        return node.ToJsonString().Replace(id.ToString(), "{id}", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Guid> InsertAsync(Guid userId, DateTimeOffset createdAt, string title = "Password changed", DateTimeOffset? readAt = null)
    {
        var row = InAppNotification.Create(
            Guid.NewGuid(), userId, Guid.NewGuid(), TypeCode, NotificationCategory.Security, title, $"Body of {title}", createdAt);
        if (readAt is { } at)
        {
            row.MarkRead(at);
        }

        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        context.Add(row);
        await context.SaveChangesAsync(Ct);

        return row.Id;
    }

    /// <summary>Inserts <paramref name="count"/> rows with one statement (not one save or one request per row); returns their ids.</summary>
    private async Task<List<Guid>> InsertManyAsync(Guid userId, int count, DateTimeOffset createdAt, DateTimeOffset? readAt = null)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var batch = Guid.NewGuid();

        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO notify.InAppNotifications (Id, UserId, NotificationId, TypeCode, Category, Title, Body, CreatedAt, ReadAt)
            SELECT TOP (@Count) NEWID(), @UserId, @BatchId, @TypeCode, 1, N'Password changed', N'Your password was changed.', @CreatedAt, @ReadAt
            FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b
            """,
            [
                new SqlParameter("Count", count),
                new SqlParameter("UserId", userId),
                new SqlParameter("BatchId", batch),
                new SqlParameter("TypeCode", TypeCode),
                new SqlParameter("CreatedAt", System.Data.SqlDbType.DateTime2) { Value = createdAt.UtcDateTime },
                new SqlParameter("ReadAt", System.Data.SqlDbType.DateTime2) { Value = (object?)readAt?.UtcDateTime ?? DBNull.Value },
            ],
            Ct);

        return await context.Set<InAppNotification>().AsNoTracking()
            .Where(row => row.NotificationId == batch)
            .Select(row => row.Id)
            .ToListAsync(Ct);
    }

    private async Task<DateTime?> ReadAtOfAsync(Guid id)
        => await QueryAsync(context => context.Set<InAppNotification>().Where(row => row.Id == id).Select(row => row.ReadAt).SingleAsync(Ct));

    private async Task<T> QueryAsync<T>(Func<NotificationsDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>());
    }

    private async Task<HttpResponseMessage> PostAsync(string route) => await Client.PostAsync(route, content: null, Ct);

    private async Task<UnreadCount> GetUnreadCountAsync()
    {
        using var response = await Client.GetAsync(UnreadCountRoute, Ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToString());
        body.EnumerateObject().Select(property => property.Name).ShouldBe(["count", "isCapped"], ignoreOrder: true);

        return new UnreadCount(body.GetProperty("count").GetInt32(), body.GetProperty("isCapped").GetBoolean());
    }

    private async Task<Page> GetPageAsync(string queryString)
    {
        using var response = await Client.GetAsync(InboxRoute + queryString, Ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToString());
        return new Page(body);
    }

    private async Task<JsonElement> GetProblemAsync(string queryString)
    {
        using var response = await Client.GetAsync(InboxRoute + queryString, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>Follows <c>nextCursor</c> from the first page until it is null.</summary>
    private async Task<List<Page>> WalkForwardAsync(string queryString)
    {
        var pages = new List<Page> { await GetPageAsync(queryString) };
        while (pages[^1].NextCursor is { } nextCursor)
        {
            pages.Count.ShouldBeLessThan(50, "the walk does not end");
            pages.Add(await GetPageAsync($"{queryString}&cursor={Uri.EscapeDataString(nextCursor)}"));
        }

        return pages;
    }

    private sealed record UnreadCount(int Count, bool IsCapped);

    private sealed record Page(JsonElement Body)
    {
        public List<JsonElement> Items => [.. Body.GetProperty("items").EnumerateArray()];

        public List<Guid> Ids => [.. Items.Select(item => item.GetProperty("id").GetGuid())];

        public string? NextCursor => Body.GetProperty("nextCursor").GetString();

        public string? PreviousCursor => Body.GetProperty("previousCursor").GetString();
    }
}
