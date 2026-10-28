using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Audit.Application;
using TemplateName.Modules.Audit.Domain.Trails;
using TemplateName.Modules.Audit.Infrastructure.Persistence;
using TemplateName.Modules.Auth;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.Persistence;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;
using TemplateName.Modules.Sample.Application;
using TemplateName.Modules.Sample.Domain.LeaveRequests;
using TemplateName.Modules.Sample.Endpoints;
using TemplateName.Modules.Sample.Infrastructure.Persistence;
using TemplateName.SharedKernel.Auditing;

namespace TemplateName.IntegrationTests.Audit;

/// <summary>
/// The audit trail from end to end in the running host: a change made through the API (or a context) is saved with its audit outbox
/// row, the owning module's outbox dispatcher (run by hand, as <c>Outbox:Enabled</c> is off in tests) publishes the change set, and the
/// Audit module writes it to <c>audit.AuditTrails</c>, where the trails endpoint or <see cref="AuditDbContext"/> reads it back.
/// </summary>
public sealed class AuditTrailEndToEndTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string LeaveRequestsRoute = "/api/v1/sample/leave-requests";
    private const string RolesRoute = "/api/v1/admin/auth/roles";
    private const string UsersRoute = "/api/v1/admin/auth/users";
    private const string LoginRoute = "/api/v1/auth/login";
    private const string TrailsRoute = "/api/v1/admin/audit/trails";
    private const string TraceIdHeader = "X-Trace-Id";

    private static readonly string AuditRowType = typeof(EntityChangesRecordedDomainEvent).FullName!;

    [Fact]
    public async Task Submitting_and_approving_a_leave_request_writes_two_change_sets_with_the_caller_and_the_trace()
    {
        var caller = await SignInAsync(
            SamplePermissions.LeaveRequestCreate,
            SamplePermissions.LeaveRequestApprove,
            AuditPermissions.TrailsView);
        var request = new SubmitLeaveRequestRequest(Guid.NewGuid(), new DateOnly(2026, 2, 2), new DateOnly(2026, 2, 4), "Family trip");

        Guid id;
        string submitTraceId;
        using (var submitted = await Client.PostAsJsonAsync(LeaveRequestsRoute, request, Ct))
        {
            submitted.StatusCode.ShouldBe(HttpStatusCode.Created);
            id = (await submitted.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
            submitTraceId = submitted.Headers.GetValues(TraceIdHeader).Single();
        }

        // A later instant, so the two sets sort apart.
        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        string approveTraceId;
        using (var approved = await Client.PostAsync($"{LeaveRequestsRoute}/{id}/approve", content: null, Ct))
        {
            approved.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            approveTraceId = approved.Headers.GetValues(TraceIdHeader).Single();
        }

        await DispatchAsync<SampleDbContext>();

        using var response = await Client.GetAsync($"{TrailsRoute}?entityType=LeaveRequest&entityId={id}&sort=occurredAt", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var items = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items").EnumerateArray().ToList();
        items.Count.ShouldBe(2);
        items[0].GetProperty("changeSetId").GetGuid().ShouldNotBe(items[1].GetProperty("changeSetId").GetGuid());

        var created = items[0];
        created.GetProperty("action").GetString().ShouldBe("Created");
        created.GetProperty("module").GetString().ShouldBe("sample");
        created.GetProperty("entityId").GetString().ShouldBe(id.ToString());
        created.GetProperty("userId").GetGuid().ShouldBe(caller.UserId);
        created.GetProperty("sessionId").GetGuid().ShouldBe(caller.SessionId);
        created.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
        created.GetProperty("traceId").GetString().ShouldBe(submitTraceId);
        var createdChanges = created.GetProperty("changes");
        createdChanges.GetProperty("Id").GetProperty("new").GetGuid().ShouldBe(id);
        createdChanges.GetProperty("EmployeeId").GetProperty("new").GetGuid().ShouldBe(request.EmployeeId);
        createdChanges.GetProperty("StartDate").GetProperty("new").GetString().ShouldBe("2026-02-02");
        createdChanges.GetProperty("EndDate").GetProperty("new").GetString().ShouldBe("2026-02-04");
        createdChanges.GetProperty("Reason").GetProperty("new").GetString().ShouldBe("Family trip");
        createdChanges.GetProperty("Status").GetProperty("new").GetString().ShouldBe("Pending");

        var updated = items[1];
        updated.GetProperty("action").GetString().ShouldBe("Updated");
        updated.GetProperty("userId").GetGuid().ShouldBe(caller.UserId);
        updated.GetProperty("sessionId").GetGuid().ShouldBe(caller.SessionId);
        updated.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
        updated.GetProperty("traceId").GetString().ShouldBe(approveTraceId);
        var updatedChanges = updated.GetProperty("changes");
        updatedChanges.EnumerateObject().Select(property => property.Name).ShouldBe(["Status", "ApproverId"], ignoreOrder: true);
        updatedChanges.GetProperty("Status").GetProperty("old").GetString().ShouldBe("Pending");
        updatedChanges.GetProperty("Status").GetProperty("new").GetString().ShouldBe("Approved");
        updatedChanges.GetProperty("ApproverId").GetProperty("old").ValueKind.ShouldBe(JsonValueKind.Null);
        updatedChanges.GetProperty("ApproverId").GetProperty("new").GetGuid().ShouldBe(caller.UserId);
    }

    [Fact]
    public async Task Soft_deleting_a_leave_request_writes_a_SoftDeleted_row()
    {
        var id = await AddLeaveRequestAsync();
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            context.Remove(await context.Set<LeaveRequest>().SingleAsync(leaveRequest => leaveRequest.Id == id, Ct));
            await context.SaveChangesAsync(Ct);
        }

        await DispatchAsync<SampleDbContext>();

        var trails = await TrailsAsync("LeaveRequest", id.ToString());
        trails.Select(trail => trail.Action).ShouldBe([EntityChangeAction.Created, EntityChangeAction.SoftDeleted], ignoreOrder: true);
        trails.Single(trail => trail.Action == EntityChangeAction.SoftDeleted).Changes.ShouldBe("{}");
    }

    [Fact]
    public async Task Changing_a_role_s_permissions_writes_created_and_deleted_grants_with_composite_ids()
    {
        await SignInAsync(AuthPermissions.RoleManage, AuthPermissions.UserView, AuthPermissions.UserLock);
        Guid roleId;
        using (var created = await Client.PostAsJsonAsync(RolesRoute, new { name = "Support", description = "Handles tickets" }, Ct))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
            roleId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        }

        var view = await PermissionIdAsync(AuthPermissions.UserView);
        var lockUsers = await PermissionIdAsync(AuthPermissions.UserLock);
        await SetPermissionsAsync(roleId, view);
        await SetPermissionsAsync(roleId, lockUsers);

        await DispatchAsync<AuthDbContext>();

        var viewGrant = await TrailsAsync("RolePermission", $"RoleId={roleId};PermissionId={view}");
        viewGrant.Select(trail => trail.Action).ShouldBe([EntityChangeAction.Created, EntityChangeAction.Deleted], ignoreOrder: true);
        viewGrant.ShouldAllBe(trail => trail.Module == "auth");
        var grantCreated = JsonDocument.Parse(viewGrant.Single(trail => trail.Action == EntityChangeAction.Created).Changes).RootElement;
        grantCreated.GetProperty("RoleId").GetProperty("new").GetGuid().ShouldBe(roleId);
        grantCreated.GetProperty("PermissionId").GetProperty("new").GetGuid().ShouldBe(view);
        var grantDeleted = JsonDocument.Parse(viewGrant.Single(trail => trail.Action == EntityChangeAction.Deleted).Changes).RootElement;
        grantDeleted.GetProperty("RoleId").GetProperty("old").GetGuid().ShouldBe(roleId);
        grantDeleted.GetProperty("PermissionId").GetProperty("old").GetGuid().ShouldBe(view);

        (await TrailsAsync("RolePermission", $"RoleId={roleId};PermissionId={lockUsers}")).Select(trail => trail.Action)
            .ShouldBe([EntityChangeAction.Created]);
    }

    [Fact]
    public async Task Suspending_a_user_writes_the_status_change_and_no_secret_or_row_version()
    {
        await SignInAsync(AuthPermissions.UserLock);
        var target = await CreateUserAsync("target@example.com");

        using (var locked = await Client.PostAsync($"{UsersRoute}/{target.Id}/lock", content: null, Ct))
        {
            locked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        await DispatchAsync<AuthDbContext>();

        var updated = (await TrailsAsync("User", target.Id.ToString())).Where(trail => trail.Action == EntityChangeAction.Updated).ShouldHaveSingleItem();
        var status = JsonDocument.Parse(updated.Changes).RootElement.GetProperty("Status");
        status.GetProperty("old").GetString().ShouldBe("Active");
        status.GetProperty("new").GetString().ShouldBe("Suspended");

        // Every trail the test wrote: the harness users and roles, the target's creation and its suspension.
        var allChanges = await AllTrailsAsync();
        allChanges.ShouldContain(trail => trail.EntityType == "User" && trail.Action == EntityChangeAction.Created);
        foreach (var trail in allChanges)
        {
            var keys = JsonDocument.Parse(trail.Changes).RootElement.EnumerateObject().Select(property => property.Name).ToList();
            keys.ShouldNotContain("PasswordHash", trail.Changes);
            keys.ShouldNotContain("SecurityStamp", trail.Changes);
            keys.ShouldNotContain("RowVersion", trail.Changes);
        }
    }

    [Fact]
    public async Task A_sign_in_that_saves_only_the_last_login_time_writes_no_audit_row()
    {
        var user = await CreateUserAsync("signin@example.com");
        var auditRowsBefore = await AuthAuditRowCountAsync();

        using (var anonymous = Factory.CreateClient())
        using (var login = await anonymous.PostAsJsonAsync(LoginRoute, new { email = "signin@example.com", password = AuthTestHarness.Password }, Ct))
        {
            login.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Set<User>().AsNoTracking()
                .SingleAsync(candidate => candidate.Id == user.Id, Ct);
            stored.LastLoginAt.ShouldNotBeNull();
        }

        (await AuthAuditRowCountAsync()).ShouldBe(auditRowsBefore);

        await DispatchAsync<AuthDbContext>();
        (await TrailsAsync("User", user.Id.ToString())).Select(trail => trail.Action).ShouldBe([EntityChangeAction.Created]);
    }

    [Fact]
    public async Task The_seeder_s_first_run_writes_trails_without_a_user_or_session()
    {
        // The base class reset the databases and seeded them again, with no current user: a first run.
        await DispatchAsync<AuthDbContext>();

        var trails = await AllTrailsAsync();
        trails.ShouldNotBeEmpty();
        trails.ShouldAllBe(trail => trail.UserId == null && trail.SessionId == null && trail.Module == "auth");
        trails.Count(trail => trail.EntityType == "Role" && trail.Action == EntityChangeAction.Created).ShouldBe(3);
        trails.ShouldContain(trail => trail.EntityType == "RolePermission" && trail.Action == EntityChangeAction.Created);

        // A later run that changes nothing audited records nothing.
        await Factory.Services.SeedAuthModuleAsync(Ct);
        (await AuthAuditRowCountAsync(unprocessedOnly: true)).ShouldBe(0);
    }

    [Fact]
    public async Task Two_stale_approvals_of_one_leave_request_leave_exactly_one_Updated_trail()
    {
        var id = await AddLeaveRequestAsync();
        await using var firstScope = Factory.Services.CreateAsyncScope();
        await using var secondScope = Factory.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<SampleDbContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<SampleDbContext>();
        var firstCopy = await first.Set<LeaveRequest>().SingleAsync(leaveRequest => leaveRequest.Id == id, Ct);
        var secondCopy = await second.Set<LeaveRequest>().SingleAsync(leaveRequest => leaveRequest.Id == id, Ct);

        firstCopy.Approve(Guid.NewGuid()).IsSuccess.ShouldBeTrue();
        secondCopy.Approve(Guid.NewGuid()).IsSuccess.ShouldBeTrue();
        await first.SaveChangesAsync(Ct);
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(Ct));

        await DispatchAsync<SampleDbContext>();

        var trails = await TrailsAsync("LeaveRequest", id.ToString());
        trails.Select(trail => trail.Action).ShouldBe([EntityChangeAction.Created, EntityChangeAction.Updated], ignoreOrder: true);
        var approver = JsonDocument.Parse(trails.Single(trail => trail.Action == EntityChangeAction.Updated).Changes).RootElement
            .GetProperty("ApproverId").GetProperty("new").GetGuid();
        approver.ShouldBe(firstCopy.ApproverId!.Value);
    }

    [Fact]
    public async Task Every_context_with_an_audited_entity_maps_the_outbox()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        DbContext[] contexts =
        [
            scope.ServiceProvider.GetRequiredService<AuthDbContext>(),
            scope.ServiceProvider.GetRequiredService<SampleDbContext>(),
            scope.ServiceProvider.GetRequiredService<NotificationsDbContext>(),
            scope.ServiceProvider.GetRequiredService<AuditDbContext>(),
        ];

        var auditing = contexts
            .Where(context => context.Model.GetEntityTypes().Any(entityType => entityType.ClrType.IsDefined(typeof(AuditedAttribute), inherit: false)))
            .ToList();

        auditing.Select(context => context.GetType()).ShouldBe([typeof(AuthDbContext), typeof(SampleDbContext)], ignoreOrder: true);
        auditing.ShouldAllBe(context => context.Model.FindEntityType(typeof(OutboxMessage)) != null);
    }

    /// <summary>Runs <typeparamref name="TContext"/>'s outbox dispatcher until it finds nothing due, so every change set reaches the Audit module.</summary>
    private async Task DispatchAsync<TContext>()
        where TContext : DbContext
    {
        const int MaxRounds = 100;
        var dispatcher = Factory.Services.GetRequiredService<OutboxDispatcher<TContext>>();
        for (var round = 0; round < MaxRounds; round++)
        {
            if (await dispatcher.ProcessBatchAsync(Ct) == 0)
            {
                return;
            }
        }

        throw new InvalidOperationException($"The {typeof(TContext).Name} outbox was still being dispatched after {MaxRounds} rounds.");
    }

    private async Task<Guid> AddLeaveRequestAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
        var leaveRequest = LeaveRequest.Submit(Guid.NewGuid(), new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 3), "Moving house", Factory.Time.GetUtcNow()).Value;
        context.Add(leaveRequest);
        await context.SaveChangesAsync(Ct);
        return leaveRequest.Id;
    }

    private async Task SetPermissionsAsync(Guid roleId, params Guid[] permissionIds)
    {
        using var response = await Client.PutAsJsonAsync($"{RolesRoute}/{roleId}/permissions", new { permissionIds }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private async Task<Guid> PermissionIdAsync(string code)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Set<Permission>()
            .Where(permission => permission.Code == code)
            .Select(permission => permission.Id)
            .SingleAsync(Ct);
    }

    private async Task<int> AuthAuditRowCountAsync(bool unprocessedOnly = false)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Set<OutboxMessage>()
            .CountAsync(message => message.Type == AuditRowType && (!unprocessedOnly || message.ProcessedAt == null), Ct);
    }

    private async Task<List<AuditTrail>> TrailsAsync(string entityType, string entityId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Set<AuditTrail>().AsNoTracking()
            .Where(trail => trail.EntityType == entityType && trail.EntityId == entityId)
            .OrderBy(trail => trail.Id)
            .ToListAsync(Ct);
    }

    private async Task<List<AuditTrail>> AllTrailsAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Set<AuditTrail>().AsNoTracking().OrderBy(trail => trail.Id).ToListAsync(Ct);
    }
}
