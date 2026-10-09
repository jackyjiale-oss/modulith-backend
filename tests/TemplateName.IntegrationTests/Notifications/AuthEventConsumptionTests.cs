using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Security;
using TemplateName.Infrastructure.Common.Inbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Notifications.Application.AuthEvents;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;
using TemplateName.SharedKernel;

namespace TemplateName.IntegrationTests.Notifications;

/// <summary>
/// The Notifications consumers of Auth's integration events in the real host: events are published through the host's
/// <see cref="IIntegrationEventPublisher"/> with an explicit id, as the Auth outbox does, and the <c>notify</c> rows are read back.
/// </summary>
public sealed class AuthEventConsumptionTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string Email = "alice@example.com";
    private const string Token = "plaintext-token-3c1f9a";
    private const string ResetLink = "http://localhost:3000/reset-password?token=" + Token;

    private static readonly string PasswordChangedConsumer = typeof(PasswordChangedIntegrationEventHandler).FullName!;

    private DateTimeOffset Now => Factory.Time.GetUtcNow();

    [Fact]
    public async Task Password_changed_creates_email_and_in_app_deliveries_in_the_user_culture()
    {
        var user = await CreateUserAsync(Email, locale: "zh-CN");
        var eventId = Guid.NewGuid();

        await PublishAsync(new PasswordChangedIntegrationEvent(eventId, Now.AddMinutes(-1), user.Id));

        var notification = (await NotificationsAsync()).ShouldHaveSingleItem();
        notification.TypeCode.ShouldBe(AuthNotificationTypes.PasswordChanged);
        notification.RecipientUserId.ShouldBe(user.Id);
        notification.SourceMessageId.ShouldBe(eventId);
        notification.Priority.ShouldBe(NotificationPriority.Critical);
        notification.Culture.ShouldBe("zh-Hans");
        notification.ProtectedData.ShouldBeNull();
        JsonSerializer.Deserialize<Dictionary<string, string>>(notification.Data).ShouldBe(new Dictionary<string, string>
        {
            ["display_name"] = "Test user",
            ["occurred_at"] = "2025-12-31 23:59",
            ["time_zone"] = "UTC",
        });

        notification.Deliveries.OrderBy(delivery => delivery.Channel).Select(delivery => (delivery.Channel, delivery.Destination, delivery.Status, delivery.NextAttemptAt))
            .ShouldBe(
            [
                (NotificationChannel.Email, (string?)Email, DeliveryStatus.Pending, (DateTime?)Now.UtcDateTime),
                (NotificationChannel.InApp, null, DeliveryStatus.Pending, Now.UtcDateTime),
            ]);
        (await InboxAsync(eventId)).ShouldBe([PasswordChangedConsumer]);
    }

    [Fact]
    public async Task Same_event_consumed_twice_creates_one_notification()
    {
        var user = await CreateUserAsync(Email);
        var passwordChanged = new PasswordChangedIntegrationEvent(Guid.NewGuid(), Now, user.Id);

        await PublishAsync(passwordChanged);
        await PublishAsync(passwordChanged);

        var notification = (await NotificationsAsync()).ShouldHaveSingleItem();
        notification.Deliveries.Count.ShouldBe(2);
        (await InboxAsync(passwordChanged.Id)).ShouldBe([PasswordChangedConsumer]);
    }

    [Fact]
    public async Task Concurrent_duplicate_consumption_creates_one_notification()
    {
        var user = await CreateUserAsync(Email);
        var events = Enumerable.Range(0, 5).Select(_ => new PasswordChangedIntegrationEvent(Guid.NewGuid(), Now, user.Id)).ToList();

        // Each event is published twice at once; neither publish may throw, and each event yields one notification.
        foreach (var passwordChanged in events)
        {
            await Task.WhenAll(PublishAsync(passwordChanged), PublishAsync(passwordChanged));
        }

        var notifications = await NotificationsAsync();
        notifications.Select(notification => notification.SourceMessageId).Order().ShouldBe(events.Select(passwordChanged => passwordChanged.Id).Order());
        notifications.ShouldAllBe(notification => notification.Deliveries.Count == 2);
    }

    [Fact]
    public async Task Stored_rows_never_contain_the_plaintext_link()
    {
        var user = await CreateUserAsync(Email);
        var protectedLink = Protect(ResetLink);
        var eventId = Guid.NewGuid();

        await PublishAsync(new PasswordResetRequestedIntegrationEvent(
            eventId, Now, user.Id, Email, protectedLink, Now.AddMinutes(30), PasswordResetReason.SelfService));

        var notification = (await NotificationsAsync()).ShouldHaveSingleItem();
        notification.TypeCode.ShouldBe(AuthNotificationTypes.PasswordReset);
        JsonSerializer.Deserialize<Dictionary<string, string>>(notification.ProtectedData!).ShouldBe(new Dictionary<string, string> { ["action_url"] = protectedLink });
        notification.ExpiresAt.ShouldBe(Now.AddMinutes(30).UtcDateTime);
        notification.Deliveries.ShouldHaveSingleItem().Destination.ShouldBe(Email);

        var stored = await ReadEveryValueAsync("Notifications", "Deliveries", "InboxMessages");
        stored.ShouldNotBeEmpty();
        stored.ShouldAllBe(value => !value.Contains(Token, StringComparison.Ordinal) && !value.Contains("localhost:3000", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("javascript:alert(document.cookie)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    public async Task Link_with_a_non_http_scheme_is_refused_and_creates_nothing(string link)
    {
        var user = await CreateUserAsync(Email);
        var verification = new EmailVerificationRequestedIntegrationEvent(Guid.NewGuid(), Now, user.Id, Email, Protect(link), Now.AddHours(1));

        await PublishAsync(verification);

        (await NotificationsAsync()).ShouldBeEmpty();
        (await InboxAsync(verification.Id)).ShouldBe([typeof(EmailVerificationRequestedIntegrationEventHandler).FullName!]);
    }

    [Fact]
    public async Task Undecryptable_link_is_refused_and_creates_nothing()
    {
        var user = await CreateUserAsync(Email);
        var verification = new EmailVerificationRequestedIntegrationEvent(Guid.NewGuid(), Now, user.Id, Email, "not-a-protected-value", Now.AddHours(1));

        await PublishAsync(verification);

        (await NotificationsAsync()).ShouldBeEmpty();
        (await InboxAsync(verification.Id)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Unknown_user_creates_nothing_and_does_not_throw()
    {
        var passwordChanged = new PasswordChangedIntegrationEvent(Guid.NewGuid(), Now, Guid.NewGuid());

        await PublishAsync(passwordChanged);

        (await NotificationsAsync()).ShouldBeEmpty();
        (await InboxAsync(passwordChanged.Id)).ShouldBe([PasswordChangedConsumer]);
    }

    private Task PublishAsync<TEvent>(TEvent integrationEvent)
        where TEvent : IIntegrationEvent
        => Factory.Services.GetRequiredService<IIntegrationEventPublisher>().PublishAsync(integrationEvent, Ct);

    private string Protect(string plaintext) => Factory.Services.GetRequiredService<ISecretProtector>().Protect(plaintext);

    private async Task<List<Notification>> NotificationsAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Set<Notification>()
            .Include(notification => notification.Deliveries)
            .AsNoTracking()
            .ToListAsync(Ct);
    }

    private async Task<List<string>> InboxAsync(Guid messageId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Set<InboxMessage>()
            .Where(message => message.MessageId == messageId)
            .Select(message => message.Consumer)
            .ToListAsync(Ct);
    }

    // Every non-null value of every column of the named notify tables, as text.
    private async Task<List<string>> ReadEveryValueAsync(params string[] tables)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var connectionString = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Database.GetConnectionString();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Ct);

        var values = new List<string>();
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM [notify].[{table}]";
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                for (var column = 0; column < reader.FieldCount; column++)
                {
                    if (!await reader.IsDBNullAsync(column, Ct))
                    {
                        values.Add(Convert.ToString(reader.GetValue(column), System.Globalization.CultureInfo.InvariantCulture)!);
                    }
                }
            }
        }

        return values;
    }
}
