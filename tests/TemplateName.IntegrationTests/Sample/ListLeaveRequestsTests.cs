using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Sample.Domain.LeaveRequests;
using TemplateName.Modules.Sample.Endpoints;
using TemplateName.Modules.Sample.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Sample;

public sealed class ListLeaveRequestsTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string BaseRoute = "/api/v1/sample/leave-requests";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await SignInAsync("sample.leave_request.view", "sample.leave_request.create");
    }

    [Fact]
    public async Task First_page_returns_items_and_next_cursor()
    {
        var seeded = await SeedAsync(25);

        var page = await GetPageAsync("?pageSize=10");

        page.Ids.ShouldBe(seeded.AsEnumerable().Reverse().Take(10));
        page.Body.GetProperty("pageSize").GetInt32().ShouldBe(10);
        page.NextCursor.ShouldNotBeNull();
        page.PreviousCursor.ShouldBeNull();
        page.Body.TryGetProperty("totalCount", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Default_page_size_is_20_and_items_carry_the_list_fields()
    {
        var employeeId = Guid.NewGuid();
        await SeedAsync(21, employeeId);

        var page = await GetPageAsync(string.Empty);

        page.Ids.Count.ShouldBe(20);
        page.Body.GetProperty("pageSize").GetInt32().ShouldBe(20);
        var item = page.Body.GetProperty("items")[0];
        item.GetProperty("employeeId").GetGuid().ShouldBe(employeeId);
        item.GetProperty("startDate").GetString().ShouldBe("2026-02-02");
        item.GetProperty("endDate").GetString().ShouldBe("2026-02-04");
        item.GetProperty("status").GetString().ShouldBe("Pending");
        item.GetProperty("createdAt").GetString().ShouldBe("2026-01-01T00:00:20Z");
        item.TryGetProperty("reason", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Walking_forward_visits_every_item_exactly_once()
    {
        var seeded = await SeedAsync(25);

        var pages = await WalkForwardAsync("?pageSize=10");

        pages.Count.ShouldBe(3);
        var ids = pages.SelectMany(page => page.Ids).ToList();
        ids.Count.ShouldBe(25);
        ids.ShouldBeUnique();
        ids.ShouldBe(seeded, ignoreOrder: true);
        var createdAts = pages.SelectMany(page => page.CreatedAts).ToList();
        createdAts.Zip(createdAts.Skip(1)).ShouldAllBe(pair => pair.First > pair.Second);
        pages[^1].Ids.Count.ShouldBe(5);
        pages[^1].NextCursor.ShouldBeNull();
        pages[^1].PreviousCursor.ShouldNotBeNull();
    }

    [Fact]
    public async Task Walking_backward_returns_the_previous_page()
    {
        await SeedAsync(25);
        var page1 = await GetPageAsync("?pageSize=10");
        var page2 = await GetPageAsync($"?pageSize=10&cursor={page1.NextCursor}");

        var back = await GetPageAsync($"?pageSize=10&cursor={page2.PreviousCursor}");

        back.Ids.ShouldBe(page1.Ids);
        back.PreviousCursor.ShouldBeNull();
        back.NextCursor.ShouldNotBeNull();
        (await GetPageAsync($"?pageSize=10&cursor={back.NextCursor}")).Ids.ShouldBe(page2.Ids);
    }

    [Fact]
    public async Task Items_inserted_after_first_page_do_not_cause_duplicates()
    {
        var seeded = await SeedAsync(25);
        var page1 = await GetPageAsync("?pageSize=10");
        Factory.Time.Advance(TimeSpan.FromSeconds(1));
        var inserted = await SubmitAsync(Guid.NewGuid(), new DateOnly(2026, 2, 2));

        var page2 = await GetPageAsync($"?pageSize=10&cursor={page1.NextCursor}");

        page2.Ids.Intersect(page1.Ids).ShouldBeEmpty();
        page2.Ids.ShouldNotContain(inserted);
        page2.Ids.ShouldBe(seeded.AsEnumerable().Reverse().Skip(10).Take(10));
    }

    [Fact]
    public async Task Ties_on_sort_value_are_broken_by_id()
    {
        var seeded = await SeedAsync(10, advanceClock: false);

        var pages = await WalkForwardAsync("?pageSize=3");

        pages.Count.ShouldBe(4);
        var ids = pages.SelectMany(page => page.Ids).ToList();
        ids.Count.ShouldBe(10);
        ids.ShouldBeUnique();
        ids.ShouldBe(seeded, ignoreOrder: true);
        pages.SelectMany(page => page.CreatedAts).Distinct().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Created_at_values_one_and_a_half_milliseconds_apart_page_without_skips_or_duplicates()
    {
        // The clock carries sub-millisecond ticks that datetime2(3) rounds away; the cursor must hold the values SQL Server stored.
        var seeded = await SeedAsync(12, step: TimeSpan.FromTicks(14_567));

        var pages = await WalkForwardAsync("?pageSize=5");
        var backward = await GetPageAsync($"?pageSize=5&cursor={pages[1].PreviousCursor}");

        var ids = pages.SelectMany(page => page.Ids).ToList();
        ids.ShouldBe(seeded.AsEnumerable().Reverse());
        backward.Ids.ShouldBe(pages[0].Ids);
    }

    [Fact]
    public async Task Empty_previous_page_after_rows_leave_the_filter_still_leads_forward()
    {
        var seeded = await SeedAsync(25);
        var newestFirst = seeded.AsEnumerable().Reverse().ToList();
        var page1 = await GetPageAsync("?status=Pending&pageSize=10");
        var page2 = await GetPageAsync($"?status=Pending&pageSize=10&cursor={page1.NextCursor}");
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            // Approving the first page's requests moves them out of status=Pending.
            var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            var approved = await db.Set<LeaveRequest>().Where(entity => page1.Ids.Contains(entity.Id)).ToListAsync(Ct);
            approved.ForEach(leaveRequest => leaveRequest.Approve(Guid.NewGuid()).IsSuccess.ShouldBeTrue());
            await db.SaveChangesAsync(Ct);
        }

        var back = await GetPageAsync($"?status=Pending&pageSize=10&cursor={page2.PreviousCursor}");

        back.Ids.ShouldBeEmpty();
        back.PreviousCursor.ShouldBeNull();
        back.NextCursor.ShouldNotBeNull();

        // The next cursor starts after the position the client came from (page 2's first item, which it has already seen).
        var forward = await GetPageAsync($"?status=Pending&pageSize=10&cursor={back.NextCursor}");
        forward.Ids.ShouldBe(newestFirst.Skip(11).Take(10));
    }

    [Fact]
    public async Task Filters_and_sort_apply()
    {
        var employeeId = Guid.NewGuid();
        var startDates = new[] { new DateOnly(2026, 3, 9), new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 1) };
        var mine = new List<Guid>();
        foreach (var startDate in startDates)
        {
            mine.Add(await SubmitAsync(employeeId, startDate));
            await SubmitAsync(Guid.NewGuid(), startDate.AddDays(-1));
            Factory.Time.Advance(TimeSpan.FromSeconds(1));
        }

        var pages = await WalkForwardAsync($"?employeeId={employeeId}&sort=startDate&pageSize=3");

        var items = pages.SelectMany(page => page.Body.GetProperty("items").EnumerateArray()).ToList();
        items.Select(item => item.GetProperty("employeeId").GetGuid()).ShouldAllBe(id => id == employeeId);
        items.Select(item => item.GetProperty("id").GetGuid()).ShouldBe(mine, ignoreOrder: true);
        items.Select(item => DateOnly.Parse(item.GetProperty("startDate").GetString()!, CultureInfo.InvariantCulture))
            .ShouldBe(startDates.Order());
    }

    [Fact]
    public async Task Status_filter_applies()
    {
        var ids = await SeedAsync(3);
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            var leaveRequest = await db.Set<LeaveRequest>().SingleAsync(entity => entity.Id == ids[1], Ct);
            leaveRequest.Approve(Guid.NewGuid()).IsSuccess.ShouldBeTrue();
            await db.SaveChangesAsync(Ct);
        }

        (await GetPageAsync("?status=Approved")).Ids.ShouldBe([ids[1]]);
        (await GetPageAsync("?status=Pending")).Ids.ShouldBe([ids[2], ids[0]]);
    }

    [Fact]
    public async Task Cursor_reused_with_different_sort_returns_400_cursor_mismatch()
    {
        await SeedAsync(5);
        var page1 = await GetPageAsync("?pageSize=2");

        var body = await GetProblemAsync($"?pageSize=2&sort=startDate&cursor={page1.NextCursor}");

        body.GetProperty("code").GetString().ShouldBe("pagination.cursor_mismatch");
    }

    [Fact]
    public async Task Cursor_reused_with_different_filter_returns_400_cursor_mismatch()
    {
        await SeedAsync(5);
        var page1 = await GetPageAsync("?pageSize=2");

        var body = await GetProblemAsync($"?pageSize=2&status=Pending&cursor={page1.NextCursor}");

        body.GetProperty("code").GetString().ShouldBe("pagination.cursor_mismatch");
    }

    [Fact]
    public async Task Tampered_cursor_returns_400_invalid_cursor()
    {
        var body = await GetProblemAsync("?cursor=abc");

        body.GetProperty("code").GetString().ShouldBe("pagination.invalid_cursor");
    }

    [Fact]
    public async Task Unknown_sort_returns_400_invalid_sort_with_allowed_fields()
    {
        var body = await GetProblemAsync("?sort=reason");

        body.GetProperty("code").GetString().ShouldBe("pagination.invalid_sort");
        body.GetProperty("params").GetProperty("allowed").GetString().ShouldBe("createdAt,startDate");
        body.GetProperty("detail").GetString()!.ShouldContain("createdAt,startDate");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Out_of_range_page_size_returns_400(int pageSize)
    {
        var body = await GetProblemAsync($"?pageSize={pageSize}");

        body.GetProperty("code").GetString().ShouldBe("validation.failed");
        body.GetProperty("errors").TryGetProperty("pageSize", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Total_count_is_returned_only_when_requested()
    {
        await SeedAsync(25);

        var page = await GetPageAsync("?pageSize=10&includeTotalCount=true");

        page.Body.GetProperty("totalCount").GetInt64().ShouldBe(25);
        page.Ids.Count.ShouldBe(10);
    }

    [Fact]
    public async Task Soft_deleted_items_are_excluded()
    {
        var seeded = await SeedAsync(7);
        var deleted = seeded[3];
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            var leaveRequest = await db.Set<LeaveRequest>().SingleAsync(entity => entity.Id == deleted, Ct);
            db.Remove(leaveRequest);
            await db.SaveChangesAsync(Ct);
        }

        var pages = await WalkForwardAsync("?pageSize=2&includeTotalCount=true");

        var ids = pages.SelectMany(page => page.Ids).ToList();
        ids.ShouldNotContain(deleted);
        ids.Count.ShouldBe(6);
        pages[0].Body.GetProperty("totalCount").GetInt64().ShouldBe(6);
    }

    [Fact]
    public async Task OpenApi_document_describes_the_pagination_query_parameters()
    {
        using var response = await Client.GetAsync("/openapi/v1.json", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        var list = document.GetProperty("paths").GetProperty(BaseRoute).GetProperty("get");
        var parameters = list.GetProperty("parameters").EnumerateArray()
            .Select(parameter => (Name: parameter.GetProperty("name").GetString(), In: parameter.GetProperty("in").GetString()))
            .ToList();

        parameters.ShouldBe(
            [
                ("employeeId", "query"),
                ("status", "query"),
                ("pageSize", "query"),
                ("cursor", "query"),
                ("sort", "query"),
                ("includeTotalCount", "query"),
            ],
            ignoreOrder: true);
        list.GetProperty("responses").TryGetProperty("200", out _).ShouldBeTrue();
        list.GetProperty("responses").TryGetProperty("400", out _).ShouldBeTrue();
    }

    /// <summary>Submits <paramref name="count"/> requests, oldest first, moving the clock by <paramref name="step"/> (1 s) between them.</summary>
    private async Task<List<Guid>> SeedAsync(int count, Guid? employeeId = null, bool advanceClock = true, TimeSpan? step = null)
    {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            if (advanceClock && i > 0)
            {
                Factory.Time.Advance(step ?? TimeSpan.FromSeconds(1));
            }

            ids.Add(await SubmitAsync(employeeId ?? Guid.NewGuid(), new DateOnly(2026, 2, 2)));
        }

        return ids;
    }

    private async Task<Guid> SubmitAsync(Guid employeeId, DateOnly startDate)
    {
        var request = new SubmitLeaveRequestRequest(employeeId, startDate, startDate.AddDays(2), "Family trip");
        using var response = await Client.PostAsJsonAsync(BaseRoute, request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return body.GetProperty("id").GetGuid();
    }

    private async Task<Page> GetPageAsync(string queryString)
    {
        using var response = await Client.GetAsync(BaseRoute + queryString, Ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToString());
        return new Page(body);
    }

    private async Task<JsonElement> GetProblemAsync(string queryString)
    {
        using var response = await Client.GetAsync(BaseRoute + queryString, Ct);
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
            pages.Add(await GetPageAsync($"{queryString}&cursor={nextCursor}"));
        }

        return pages;
    }

    private sealed record Page(JsonElement Body)
    {
        public List<Guid> Ids => [.. Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())];

        public List<DateTime> CreatedAts =>
        [
            .. Body.GetProperty("items").EnumerateArray()
                .Select(item => DateTime.Parse(item.GetProperty("createdAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
        ];

        public string? NextCursor => Body.GetProperty("nextCursor").GetString();

        public string? PreviousCursor => Body.GetProperty("previousCursor").GetString();
    }
}
