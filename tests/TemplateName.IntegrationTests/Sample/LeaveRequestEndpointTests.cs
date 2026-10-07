using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TemplateName.Application.Common.Messaging;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Sample.Application.LeaveRequests.GetById;
using TemplateName.Modules.Sample.Domain.LeaveRequests;
using TemplateName.Modules.Sample.Domain.LeaveRequests.Events;
using TemplateName.Modules.Sample.Endpoints;
using TemplateName.Modules.Sample.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Sample;

public sealed class LeaveRequestEndpointTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string BaseRoute = "/api/v1/sample/leave-requests";

    [Fact]
    public async Task Submit_returns_201_with_location_and_id()
    {
        using var response = await Client.PostAsJsonAsync(BaseRoute, Valid(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = body.GetProperty("id").GetGuid();
        id.ShouldNotBe(Guid.Empty);
        response.Headers.Location!.OriginalString.ShouldBe($"/api/v1/sample/leave-requests/{id}");
    }

    [Fact]
    public async Task Get_returns_submitted_request()
    {
        var request = Valid();
        var id = await SubmitAsync(request);

        using var response = await Client.GetAsync($"{BaseRoute}/{id}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("id").GetGuid().ShouldBe(id);
        body.GetProperty("employeeId").GetGuid().ShouldBe(request.EmployeeId);
        DateOnly.Parse(body.GetProperty("startDate").GetString()!, CultureInfo.InvariantCulture).ShouldBe(request.StartDate);
        DateOnly.Parse(body.GetProperty("endDate").GetString()!, CultureInfo.InvariantCulture).ShouldBe(request.EndDate);
        body.GetProperty("reason").GetString().ShouldBe(request.Reason);
        body.GetProperty("status").GetString().ShouldBe("Pending");
        body.GetProperty("createdAt").GetString().ShouldEndWith("Z");
    }

    [Fact]
    public async Task Get_unknown_returns_404_leave_not_found()
    {
        using var response = await Client.GetAsync($"{BaseRoute}/{Guid.NewGuid()}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe("leave.not_found");
        body.GetProperty("traceId").GetString().ShouldBe(response.Headers.GetValues("X-Trace-Id").Single());
    }

    [Fact]
    public async Task Submit_invalid_returns_400_with_camel_case_errors()
    {
        using var response = await Client.PostAsJsonAsync(BaseRoute, Valid() with { Reason = "" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe("validation.failed");
        body.GetProperty("errors").TryGetProperty("reason", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Approve_twice_returns_409_leave_not_pending()
    {
        var id = await SubmitAsync(Valid());
        var approve = new ApproveLeaveRequestRequest(Guid.NewGuid());

        using var first = await Client.PostAsJsonAsync($"{BaseRoute}/{id}/approve", approve, Ct);
        using var second = await Client.PostAsJsonAsync($"{BaseRoute}/{id}/approve", approve, Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe("leave.not_pending");
    }

    [Fact]
    public async Task Malformed_json_returns_400_problem_details()
    {
        using var content = new StringContent("{\"employeeId\": \"not-a-guid\"", Encoding.UTF8, "application/json");

        using var response = await Client.PostAsync(BaseRoute, content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe("request.malformed");
        body.GetProperty("traceId").GetString().ShouldBe(response.Headers.GetValues("X-Trace-Id").Single());
    }

    [Fact]
    public async Task Unhandled_exception_returns_500_without_stack_trace()
    {
        // A substitute, not a handler class: the factory scans this assembly for handlers and would register a class for every host.
        var throwingHandler = Substitute.For<IQueryHandler<GetLeaveRequestByIdQuery, LeaveRequestResponse>>();
        throwingHandler.HandleAsync(Arg.Any<GetLeaveRequestByIdQuery>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Simulated failure in the leave request query."));
        await using var throwing = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IQueryHandler<GetLeaveRequestByIdQuery, LeaveRequestResponse>>();
            services.AddSingleton(throwingHandler);
        }));
        using var client = throwing.CreateClient();

        using var response = await client.GetAsync($"{BaseRoute}/{Guid.NewGuid()}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var text = await response.Content.ReadAsStringAsync(Ct);
        JsonDocument.Parse(text).RootElement.GetProperty("code").GetString().ShouldBe("server.unexpected_error");
        text.ShouldNotContain("   at ");
    }

    [Fact]
    public async Task Soft_deleted_request_is_not_returned()
    {
        var id = await SubmitAsync(Valid());
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            var leaveRequest = await db.Set<LeaveRequest>().SingleAsync(entity => entity.Id == id, Ct);
            db.Remove(leaveRequest);
            await db.SaveChangesAsync(Ct);
        }

        using var response = await Client.GetAsync($"{BaseRoute}/{id}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Submitted_event_is_dispatched_through_outbox()
    {
        var id = await SubmitAsync(Valid());
        var dispatcher = Factory.Services.GetRequiredService<OutboxDispatcher<SampleDbContext>>();

        (await dispatcher.ProcessBatchAsync(Ct)).ShouldBeGreaterThanOrEqualTo(1);

        Factory.EventRecorder.Events.OfType<LeaveRequestSubmittedDomainEvent>().ShouldHaveSingleItem().LeaveRequestId.ShouldBe(id);
    }

    private static SubmitLeaveRequestRequest Valid()
        => new(Guid.NewGuid(), new DateOnly(2026, 2, 2), new DateOnly(2026, 2, 4), "Family trip");

    private async Task<Guid> SubmitAsync(SubmitLeaveRequestRequest request)
    {
        using var response = await Client.PostAsJsonAsync(BaseRoute, request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return body.GetProperty("id").GetGuid();
    }
}
