using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TemplateName.Application.Common.Messaging;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Outbox;

/// <summary>
/// A module may publish an integration event that no module consumes: a project generated with <c>--notifications false</c> keeps Auth's
/// events (verification, password reset, sign-in notices) with nobody listening. The outbox must complete such a message on its first
/// dispatch, not retry it or leave it pending.
/// </summary>
public sealed class OutboxWithoutHandlersTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string RegisterRoute = "/api/v1/auth/register";

    [Fact]
    public async Task Message_for_an_event_without_a_handler_is_marked_processed()
    {
        // A host with no integration event consumer at all: every module's and every test's registration is removed.
        await using var host = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.RemoveAll<IntegrationEventHandlerRegistration>()));
        using var client = host.CreateClient();

        using (var response = await client.PostAsJsonAsync(
            RegisterRoute,
            new { email = "alice@example.com", password = AuthTestHarness.Password, displayName = "Alice", locale = "en" },
            Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        await DispatchUntilIdleAsync(host.Services);

        await using var scope = host.Services.CreateAsyncScope();
        var messages = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Set<OutboxMessage>().AsNoTracking()
            .Select(message => new { message.Type, message.ProcessedAt, message.AttemptCount })
            .ToListAsync(Ct);
        messages.ShouldNotBeEmpty();
        messages.ShouldAllBe(message => message.ProcessedAt != null);
        messages.ShouldAllBe(message => message.AttemptCount == 0);
    }

    private static async Task DispatchUntilIdleAsync(IServiceProvider services)
    {
        const int MaxRounds = 100;
        var outbox = services.GetRequiredService<OutboxDispatcher<AuthDbContext>>();
        for (var round = 0; round < MaxRounds; round++)
        {
            if (await outbox.ProcessBatchAsync(Ct) == 0)
            {
                return;
            }
        }

        throw new InvalidOperationException($"The Auth outbox was still being dispatched after {MaxRounds} rounds.");
    }
}
