using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.Persistence;
using TemplateName.Modules.Sample.Domain.LeaveRequests;
using TemplateName.Modules.Sample.Endpoints;
using TemplateName.Modules.Sample.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Sample;

/// <summary>
/// The Sample endpoints under authorization: each route requires its permission (<c>sample.leave_request.view</c>, <c>.create</c>,
/// <c>.approve</c>), an anonymous caller gets 401 and a signed-in caller without the permission 403, and the approver is the caller.
/// </summary>
public sealed class SampleAuthorizationTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string BaseRoute = "/api/v1/sample/leave-requests";
    private const string View = "sample.leave_request.view";
    private const string Create = "sample.leave_request.create";
    private const string Approve = "sample.leave_request.approve";

    [Fact]
    public async Task Anonymous_request_to_leave_requests_is_401()
    {
        var id = await SubmitAsSomeoneElseAsync();

        foreach (var request in EveryRoute(id))
        {
            using (request)
            using (var response = await Client.SendAsync(request, Ct))
            {
                await ShouldBeProblemAsync(response, HttpStatusCode.Unauthorized, "http.401");
                response.Headers.WwwAuthenticate.ToString().ShouldStartWith("Bearer");
            }
        }

        (await ReadAsync(id)).Status.ShouldBe(LeaveRequestStatus.Pending);
    }

    [Fact]
    public async Task Signed_in_user_without_permission_gets_403_problem_details_with_code_http_403()
    {
        var id = await SubmitAsSomeoneElseAsync();
        await SignInAsync();

        foreach (var request in EveryRoute(id))
        {
            using (request)
            using (var response = await Client.SendAsync(request, Ct))
            {
                await ShouldBeProblemAsync(response, HttpStatusCode.Forbidden, "http.403");
            }
        }

        (await ReadAsync(id)).Status.ShouldBe(LeaveRequestStatus.Pending);
        (await CountLeaveRequestsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Submit_requires_create_permission()
    {
        await SignInAsync(View, Approve);

        using (var forbidden = await Client.PostAsJsonAsync(BaseRoute, Valid(), Ct))
        {
            await ShouldBeProblemAsync(forbidden, HttpStatusCode.Forbidden, "http.403");
        }

        (await CountLeaveRequestsAsync()).ShouldBe(0);

        await SignInAsync(Create);
        using var created = await Client.PostAsJsonAsync(BaseRoute, Valid(), Ct);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await CountLeaveRequestsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task List_and_get_require_view_permission()
    {
        await SignInAsync(Create, Approve);
        var id = await SubmitAsync();

        using (var list = await Client.GetAsync(BaseRoute, Ct))
        {
            await ShouldBeProblemAsync(list, HttpStatusCode.Forbidden, "http.403");
        }

        using (var get = await Client.GetAsync($"{BaseRoute}/{id}", Ct))
        {
            await ShouldBeProblemAsync(get, HttpStatusCode.Forbidden, "http.403");
        }

        await SignInAsync(View);

        using (var list = await Client.GetAsync(BaseRoute, Ct))
        {
            list.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await list.Content.ReadFromJsonAsync<JsonElement>(Ct);
            body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldBe([id]);
        }

        using var get200 = await Client.GetAsync($"{BaseRoute}/{id}", Ct);
        get200.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await get200.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid().ShouldBe(id);
    }

    [Fact]
    public async Task Approve_records_the_signed_in_user_as_approver()
    {
        var approver = await SignInAsync(Create, Approve, View);
        var id = await SubmitAsync();

        using var response = await Client.PostAsync($"{BaseRoute}/{id}/approve", content: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var stored = await ReadAsync(id);
        stored.Status.ShouldBe(LeaveRequestStatus.Approved);
        stored.ApproverId.ShouldBe(approver.UserId);
        using var get = await Client.GetAsync($"{BaseRoute}/{id}", Ct);
        (await get.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("approverId").GetGuid().ShouldBe(approver.UserId);
    }

    [Fact]
    public async Task Approve_ignores_any_approver_id_in_a_body()
    {
        var approver = await SignInAsync(Create, Approve);
        var id = await SubmitAsync();
        var someoneElse = Guid.NewGuid();

        using var response = await Client.PostAsJsonAsync($"{BaseRoute}/{id}/approve", new { approverId = someoneElse }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var stored = await ReadAsync(id);
        stored.Status.ShouldBe(LeaveRequestStatus.Approved);
        stored.ApproverId.ShouldBe(approver.UserId);
        stored.ApproverId.ShouldNotBe(someoneElse);
    }

    [Fact]
    public async Task Permission_granted_through_a_role_works_and_removing_it_takes_effect_on_the_next_request()
    {
        var user = await SignInAsync(View);

        using (var allowed = await Client.GetAsync(BaseRoute, Ct))
        {
            allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await RemoveEveryGrantAsync(user.UserId);

        // The checker caches the user's permissions (30 s); the grant is gone, but this instance has not been told yet.
        using (var cached = await Client.GetAsync(BaseRoute, Ct))
        {
            cached.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPermissionCache>().InvalidateUsersAsync([user.UserId], Ct);
        }

        using var forbidden = await Client.GetAsync(BaseRoute, Ct);
        await ShouldBeProblemAsync(forbidden, HttpStatusCode.Forbidden, "http.403");
    }

    /// <summary>One request per route: list, get by id, submit and approve (approve without a body).</summary>
    private static HttpRequestMessage[] EveryRoute(Guid id) =>
    [
        new(HttpMethod.Get, BaseRoute),
        new(HttpMethod.Get, $"{BaseRoute}/{id}"),
        new(HttpMethod.Post, BaseRoute) { Content = JsonContent.Create(Valid()) },
        new(HttpMethod.Post, $"{BaseRoute}/{id}/approve"),
    ];

    private static SubmitLeaveRequestRequest Valid()
        => new(Guid.NewGuid(), new DateOnly(2026, 2, 2), new DateOnly(2026, 2, 4), "Family trip");

    private static async Task ShouldBeProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("status").GetInt32().ShouldBe((int)status);
        body.GetProperty("code").GetString().ShouldBe(code);
        body.GetProperty("traceId").GetString().ShouldBe(response.Headers.GetValues("X-Trace-Id").Single());
    }

    private async Task<Guid> SubmitAsync()
    {
        using var response = await Client.PostAsJsonAsync(BaseRoute, Valid(), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    /// <summary>A pending request submitted by another signed-in user; the test's own client stays signed out.</summary>
    private async Task<Guid> SubmitAsSomeoneElseAsync()
    {
        var submitter = await SignInAsync(Create);
        var id = await SubmitAsync();
        SignOut();
        submitter.UserId.ShouldNotBe(Guid.Empty);
        return id;
    }

    private async Task<LeaveRequest> ReadAsync(Guid id)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SampleDbContext>().Set<LeaveRequest>().AsNoTracking()
            .SingleAsync(leaveRequest => leaveRequest.Id == id, Ct);
    }

    private async Task<int> CountLeaveRequestsAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SampleDbContext>().Set<LeaveRequest>().CountAsync(Ct);
    }

    /// <summary>Empties the permission set of every role the user holds, as an administrator editing the role would.</summary>
    private async Task RemoveEveryGrantAsync(Guid userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var roleIds = await context.Set<UserRole>().Where(assignment => assignment.UserId == userId).Select(assignment => assignment.RoleId)
            .ToListAsync(Ct);
        var roles = await context.Set<Role>().Include(role => role.Permissions).Where(role => roleIds.Contains(role.Id)).ToListAsync(Ct);
        roles.ShouldNotBeEmpty();
        foreach (var role in roles)
        {
            role.SetPermissions([]).IsSuccess.ShouldBeTrue();
        }

        await context.SaveChangesAsync(Ct);
    }
}
