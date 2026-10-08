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
using TemplateName.Infrastructure.Common.Idempotency;
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

    // The default Idempotency:InProgressTimeout: how long an in-progress key stays leased to the request that inserted it.
    private static readonly TimeSpan InProgressTimeout = TimeSpan.FromMinutes(5);

    private SignedInUser? _user;

    private SignedInUser User => _user ?? throw new InvalidOperationException("InitializeAsync signs the user in.");

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // Keys are scoped to the signed-in user, and every host below sends this user's token.
        _user = await SignInAsync("sample.leave_request.create");
    }

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
        await RenewAccessTokenAsync();
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
        using var client = await CreateClientAsync(parking);
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
        using var throwingClient = await CreateClientAsync(throwing);
        var request = Valid();

        using var failed = await PostAsync(throwingClient, request, Key);
        using var retried = await PostAsync(Client, request, Key);

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        await ShouldBeProblemAsync(failed, "server.unexpected_error");
        retried.StatusCode.ShouldBe(HttpStatusCode.Created);
        retried.Headers.Contains(ReplayedHeader).ShouldBeFalse();
        (await CountLeaveRequestsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Empty_key_returns_400()
    {
        // A header with an empty value does not reach the server through HttpClient, so the request is built on the test server.
        var body = JsonSerializer.SerializeToUtf8Bytes(Valid(), JsonSerializerOptions.Web);
        var context = await Factory.Server.SendAsync(
            http =>
            {
                http.Request.Method = HttpMethod.Post.Method;
                http.Request.Path = SubmitRoute;
                http.Request.ContentType = "application/json";
                http.Request.Headers[KeyHeader] = string.Empty;
                http.Request.Headers.Authorization = $"Bearer {User.AccessToken}";
                http.Request.Body = new MemoryStream(body);
            },
            Ct);

        context.Response.StatusCode.ShouldBe((int)HttpStatusCode.BadRequest);
        var problem = await JsonSerializer.DeserializeAsync<JsonElement>(context.Response.Body, cancellationToken: Ct);
        problem.GetProperty("code").GetString().ShouldBe("idempotency.invalid_key");
        (await CountLeaveRequestsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Repeated_key_header_returns_400()
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, SubmitRoute) { Content = JsonContent.Create(Valid()) };
        message.Headers.TryAddWithoutValidation(KeyHeader, [Key, "submit-leave-request-2"]).ShouldBeTrue();

        using var response = await Client.SendAsync(message, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await ShouldBeProblemAsync(response, "idempotency.invalid_key");
        (await CountLeaveRequestsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Stored_client_error_is_replayed()
    {
        // The end date is before the start date, so the validator answers 400; a 4xx is stored like any response below 500.
        var invalid = new SubmitLeaveRequestRequest(Guid.NewGuid(), new DateOnly(2026, 2, 4), new DateOnly(2026, 2, 2), "Backwards");

        using var first = await PostAsync(Client, invalid, Key);
        using var second = await PostAsync(Client, invalid, Key);

        first.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await ShouldBeProblemAsync(first, "validation.failed");
        first.Headers.Contains(ReplayedHeader).ShouldBeFalse();
        second.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        second.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await second.Content.ReadAsStringAsync(Ct)).ShouldBe(await first.Content.ReadAsStringAsync(Ct));
        second.Headers.GetValues(ReplayedHeader).ShouldHaveSingleItem().ShouldBe("true");
    }

    [Fact]
    public async Task Server_error_response_without_exception_is_not_stored()
    {
        var failingHandler = Substitute.For<ICommandHandler<SubmitLeaveRequestCommand, Guid>>();
        failingHandler.HandleAsync(Arg.Any<SubmitLeaveRequestCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>(Error.Failure("test.simulated_failure", "Simulated failure result.")));
        await using var failing = WithSubmitHandler(failingHandler);
        using var failingClient = await CreateClientAsync(failing);
        var request = Valid();

        using var failed = await PostAsync(failingClient, request, Key);
        var recordAfterFailure = await ReadRecordAsync();
        using var retried = await PostAsync(Client, request, Key);

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        recordAfterFailure.ShouldBeNull();
        retried.StatusCode.ShouldBe(HttpStatusCode.Created);
        retried.Headers.Contains(ReplayedHeader).ShouldBeFalse();
        (await CountLeaveRequestsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Abandoned_in_progress_key_is_reusable_after_in_progress_timeout()
    {
        // The first request parks inside the handler, standing in for a request whose process hangs or dies mid-way.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<Result<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var parking = WithSubmitHandler(ParkingHandler(entered, release.Task));
        using var parkingClient = await CreateClientAsync(parking);
        var request = Valid();

        var abandonedTask = PostAsync(parkingClient, request, Key);
        await entered.Task.WaitAsync(Ct);
        using var beforeTimeout = await PostAsync(Client, request, Key);

        Factory.Time.Advance(InProgressTimeout + TimeSpan.FromSeconds(1));
        using var afterTimeout = await PostAsync(Client, request, Key);

        // The superseded first request finishes late; its response must not replace the new owner's stored one.
        release.SetResult(Result.Success(Guid.NewGuid()));
        using var abandoned = await abandonedTask;
        using var replayed = await PostAsync(Client, request, Key);

        beforeTimeout.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await ShouldBeProblemAsync(beforeTimeout, "idempotency.in_progress");
        afterTimeout.StatusCode.ShouldBe(HttpStatusCode.Created);
        afterTimeout.Headers.Contains(ReplayedHeader).ShouldBeFalse();
        abandoned.StatusCode.ShouldBe(HttpStatusCode.Created);
        replayed.StatusCode.ShouldBe(HttpStatusCode.Created);
        replayed.Headers.GetValues(ReplayedHeader).ShouldHaveSingleItem().ShouldBe("true");
        replayed.Headers.Location.ShouldBe(afterTimeout.Headers.Location);
        (await CountLeaveRequestsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Completed_response_is_kept_for_time_to_live()
    {
        var request = Valid();
        using var first = await PostAsync(Client, request, Key);

        // Past the in-progress lease, well within the one-day time to live.
        Factory.Time.Advance(InProgressTimeout + TimeSpan.FromMinutes(1));
        using var second = await PostAsync(Client, request, Key);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.Headers.GetValues(ReplayedHeader).ShouldHaveSingleItem().ShouldBe("true");
        second.Headers.Location.ShouldBe(first.Headers.Location);
        (await CountLeaveRequestsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Superseded_owner_does_not_store_its_response()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<Result<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var parking = WithSubmitHandler(ParkingHandler(entered, release.Task));
        using var parkingClient = await CreateClientAsync(parking);
        var request = Valid();

        var supersededTask = PostAsync(parkingClient, request, Key);
        await entered.Task.WaitAsync(Ct);

        // As if the lease had expired and another request had taken the key over and were still running.
        var newOwner = Guid.NewGuid();
        await SetLockIdAsync(newOwner);
        release.SetResult(Result.Success(Guid.NewGuid()));
        using var superseded = await supersededTask;
        var record = await ReadRecordAsync();
        using var retried = await PostAsync(Client, request, Key);

        superseded.StatusCode.ShouldBe(HttpStatusCode.Created);
        record.ShouldNotBeNull();
        record.LockId.ShouldBe(newOwner);
        record.StatusCode.ShouldBeNull();
        retried.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await ShouldBeProblemAsync(retried, "idempotency.in_progress");
    }

    [Fact]
    public async Task Superseded_owner_does_not_release_the_new_owners_key()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<Result<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var parking = WithSubmitHandler(ParkingHandler(entered, release.Task));
        using var parkingClient = await CreateClientAsync(parking);
        var request = Valid();

        var supersededTask = PostAsync(parkingClient, request, Key);
        await entered.Task.WaitAsync(Ct);

        var newOwner = Guid.NewGuid();
        await SetLockIdAsync(newOwner);
        release.SetException(new InvalidOperationException("Simulated failure in the superseded request."));
        using var superseded = await supersededTask;
        var record = await ReadRecordAsync();
        using var retried = await PostAsync(Client, request, Key);

        superseded.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        record.ShouldNotBeNull();
        record.LockId.ShouldBe(newOwner);
        retried.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await ShouldBeProblemAsync(retried, "idempotency.in_progress");
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

    private static ICommandHandler<SubmitLeaveRequestCommand, Guid> ParkingHandler(TaskCompletionSource entered, Task<Result<Guid>> release)
    {
        var handler = Substitute.For<ICommandHandler<SubmitLeaveRequestCommand, Guid>>();
        handler.HandleAsync(Arg.Any<SubmitLeaveRequestCommand>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                entered.SetResult();
                return await release;
            });
        return handler;
    }

    private async Task<IdempotencyRecord?> ReadRecordAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().IdempotencyKeys.AsNoTracking()
            .SingleOrDefaultAsync(record => record.Key == Key, Ct);
    }

    private async Task SetLockIdAsync(Guid lockId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var updated = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().IdempotencyKeys
            .Where(record => record.Key == Key)
            .ExecuteUpdateAsync(setters => setters.SetProperty(record => record.LockId, lockId), Ct);
        updated.ShouldBe(1);
    }

    private async Task<int> CountLeaveRequestsAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SampleDbContext>().Set<LeaveRequest>().CountAsync(Ct);
    }
}
