using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Hubs;
using TemplateName.UnitTests.Application;

namespace TemplateName.UnitTests.Notifications;

/// <summary>
/// The in-app pusher is best effort (ADR 0021): a failed send, such as the Redis backplane's while Redis is down, is logged and never
/// thrown; only the caller's own cancellation propagates.
/// </summary>
public sealed class SignalRInAppNotificationPusherTests
{
    private static readonly Guid UserId = Guid.Parse("8b3c6f5e-2a41-4d7e-9c0b-1e2f3a4b5c6d");

    private readonly IClientProxy _user = Substitute.For<IClientProxy>();
    private readonly RecordingLogger<SignalRInAppNotificationPusher> _logger = new();
    private readonly SignalRInAppNotificationPusher _pusher;

    public SignalRInAppNotificationPusherTests()
    {
        var hub = Substitute.For<IHubContext<NotificationsHub>>();
        hub.Clients.User(UserId.ToString()).Returns(_user);
        _pusher = new SignalRInAppNotificationPusher(hub, _logger);
    }

    [Fact]
    public async Task Sends_the_payload_to_the_users_connections()
    {
        var payload = Payload();

        await _pusher.PushAsync(UserId, payload, TestContext.Current.CancellationToken);

        await _user.Received(1).SendCoreAsync(
            NotificationsHub.NotificationReceived,
            Arg.Is<object?[]>(arguments => arguments.Length == 1 && ReferenceEquals(arguments[0], payload)),
            TestContext.Current.CancellationToken);
        _logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_send_is_logged_with_its_type_and_not_thrown()
    {
        _user.SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("PUBLISH ...:user:secret-channel")));
        var payload = Payload();

        await Should.NotThrowAsync(() => _pusher.PushAsync(UserId, payload, TestContext.Current.CancellationToken));

        var entry = _logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Properties["InAppNotificationId"].ShouldBe(payload.Id);
        entry.Properties["ExceptionType"].ShouldBe(nameof(InvalidOperationException));
        entry.Message.ShouldNotContain("secret-channel");
    }

    [Fact]
    public async Task The_callers_cancellation_propagates_and_is_not_logged()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _user.SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromCanceled(cancellation.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => _pusher.PushAsync(UserId, Payload(), cancellation.Token));

        _logger.Entries.ShouldBeEmpty();
    }

    private static InAppNotificationPushed Payload() => new(
        Guid.NewGuid(), "auth.password_changed", NotificationCategory.Security, "Title", "Body", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
}
