using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TemplateName.Application.Common.Messaging;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Sample.Application.LeaveRequests.Submit;
using TemplateName.Modules.Sample.Domain.LeaveRequests;
using TemplateName.Modules.Sample.Endpoints;
using TemplateName.Modules.Sample.Infrastructure.Persistence;
using TemplateName.SharedKernel;

namespace TemplateName.IntegrationTests.Idempotency;

public sealed class IdempotencyTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string SubmitRoute = "/api/v1/sample/leave-requests";
    private const string KeyHeader = "Idempotency-Key";
    private const string ReplayedHeader = "Idempotency-Replayed";

    // Every test uses the same key, so a test passes only if the reset between tests cleared platform.IdempotencyKeys.
    private const string Key = "submit-leave-request-1";

    [Fact]
    public async Task Same_key_same_body_replays_response()
    {
        var request = Valid();

        using var first = await PostAsync(Client, request, Key);
        using var second = await PostAsync(Client, request, Key);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.Headers.Location.ShouldNotBeNull();
        second.Headers.Location.ShouldBe(first.Headers.Location);
        second.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        (await second.Content.ReadAsStringAsync(Ct)).ShouldBe(await first.Content.ReadAsStringAsync(Ct));
        first.Headers.Contains(ReplayedHeader).ShouldBeFalse();
        second.Headers.GetValues(ReplayedHeader).ShouldHaveSingleItem().ShouldBe("true");
        (await CountLeaveRequestsAsync()).ShouldBe(1);

        // Buffering and replaying must keep the headers other middleware add when the response starts.
        foreach (var response in new[] { first, second })
        {
            response.Headers.Contains("X-Trace-Id").ShouldBeTrue();
            response.Headers.Contains("X-Content-Type-Options").ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Same_key_different_body_returns_422()
    {
        using var first = await PostAsync(Client, Valid(), Key);
        using var second = await PostAsync(Client, Valid(), Key);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        await ShouldBeProblemAsync(second, "idempotency.key_reused");
        (await CountLeaveRequestsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Requests_without_key_execute_each_time()
    {
        var request = Valid();

        using var first = await Client.PostAsJsonAsync(SubmitRoute, request, Ct);
        using var second = await Client.PostAsJsonAsync(SubmitRoute, request, Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.Headers.Location.ShouldNotBe(first.Headers.Location);
        (await CountLeaveRequestsAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Invalid_key_returns_400()
    {
        using var response = await PostAsync(Client, Valid(), new string('k', 101));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await ShouldBeProblemAsync(response, "idempotency.invalid_key");
        (await CountLeaveRequestsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Expired_key_executes_again()
    {
        var request = Valid();
        using var first = await PostAsync(Client, request, Key);

        Factory.Time.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        using var second = await PostAsync(Client, request, Key);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.Headers.Location.ShouldNotBe(first.Headers.Location);
        second.Headers.Contains(ReplayedHeader).ShouldBeFalse();
        (await CountLeaveRequestsAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Concurrent_requests_with_same_key_execute_once()
    {
        var request = Valid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => PostAsync(Client, request, Key)));
        try
        {
            responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.Conflict);
            responses.ShouldContain(response => response.StatusCode == HttpStatusCode.Created);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        (await CountLeaveRequestsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Same_key_while_in_progress_returns_409()
    {
        // The first request parks inside the handler, so its key is in progress when the second one arrives.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parkingHandler = Substitute.For<ICommandHandler<SubmitLeaveRequestCommand, Guid>>();
        parkingHandler.HandleAsync(Arg.Any<SubmitLeaveRequestCommand>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                entered.SetResult();
                await release.Task;
                return Result.Success(Guid.NewGuid());
            });
        await using var parking = WithSubmitHandler(parkingHandler);
        using var client = parking.CreateClient();
        var request = Valid();

        var firstTask = PostAsync(client, request, Key);
        await entered.Task.WaitAsync(Ct);
        using var second = await PostAsync(client, request, Key);
        release.SetResult();
        using var first = await firstTask;

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await ShouldBeProblemAsync(second, "idempotency.in_progress");
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Server_error_is_not_stored()
    {
        // A substitute, not a handler class: the factory scans this assembly for handlers and would register a class for every host.
        var throwingHandler = Substitute.For<ICommandHandler<SubmitLeaveRequestCommand, Guid>>();
        throwingHandler.HandleAsync(Arg.Any<SubmitLeaveRequestCommand>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Simulated failure in the submit handler."));
        await using var throwing = WithSubmitHandler(throwingHandler);
        using var throwingClient = throwing.CreateClient();
        var request = Valid();

        using var failed = await PostAsync(throwingClient, request, Key);
        using var retried = await PostAsync(Client, request, Key);

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        await ShouldBeProblemAsync(failed, "server.unexpected_error");
        retried.StatusCode.ShouldBe(HttpStatusCode.Created);
        retried.Headers.Contains(ReplayedHeader).ShouldBeFalse();
        (await CountLeaveRequestsAsync()).ShouldBe(1);
    }

    private static SubmitLeaveRequestRequest Valid()
        => new(Guid.NewGuid(), new DateOnly(2026, 2, 2), new DateOnly(2026, 2, 4), "Family trip");

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, SubmitLeaveRequestRequest request, string key)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, SubmitRoute) { Content = JsonContent.Create(request) };
        message.Headers.Add(KeyHeader, key);
        return await client.SendAsync(message, Ct);
    }

    private static async Task ShouldBeProblemAsync(HttpResponseMessage response, string code)
    {
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe(code);
        body.GetProperty("traceId").GetString().ShouldBe(response.Headers.GetValues("X-Trace-Id").Single());
    }

    private WebApplicationFactory<Program> WithSubmitHandler(ICommandHandler<SubmitLeaveRequestCommand, Guid> handler)
        => Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICommandHandler<SubmitLeaveRequestCommand, Guid>>();
            services.AddSingleton(handler);
        }));

    private async Task<int> CountLeaveRequestsAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SampleDbContext>().Set<LeaveRequest>().CountAsync(Ct);
    }
}
