using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TemplateName.Modules.Auth.Contracts.Users;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Domain.PushDevices;
using TemplateName.Modules.Notifications.Infrastructure.Channels.Push;

namespace TemplateName.UnitTests.Notifications.Push;

public sealed class PushChannelTests
{
    private const string Token = "fcm-device-token";

    private static readonly DateTimeOffset Now = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);

    private static readonly Guid UserId = Guid.NewGuid();

    private readonly IPushDeviceRepository _devices = Substitute.For<IPushDeviceRepository>();

    private readonly IUserSessionDirectory _sessions = Substitute.For<IUserSessionDirectory>();

    private readonly IPushTransport _transport = Substitute.For<IPushTransport>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_active_device_with_a_live_session_is_sent_to()
    {
        var device = Device();
        PushMessage? sent = null;
        await _transport.SendAsync(Arg.Do<PushMessage>(message => sent = message), Arg.Any<CancellationToken>());

        var result = await Channel().SendAsync(Work(device), Ct);

        result.ShouldBe(ChannelSendResult.Sent);
        sent.ShouldNotBeNull();
        sent.Token.ShouldBe(Token);
        sent.DeviceId.ShouldBe(device.Id);
        sent.Title.ShouldBe("Security alert");
        sent.Body.ShouldBe("Someone used an old session.");
        sent.Data["typeCode"].ShouldBe("auth.token_reuse_detected");
        sent.Data["category"].ShouldBe("Security");
        sent.Data.ShouldContainKey("notificationId");
    }

    [Fact]
    public async Task A_missing_device_is_device_not_found()
    {
        var result = await Channel().SendAsync(Work(Device(), destination: PushDestination.For(Guid.NewGuid())), Ct);

        result.ShouldBe(ChannelSendResult.Permanent("device_not_found"));
    }

    [Fact]
    public async Task An_inactive_device_is_device_inactive()
    {
        var device = Device();
        device.Deactivate(PushDeviceDeactivationReason.RemovedByUser, Now.AddHours(-1));

        (await Channel().SendAsync(Work(device), Ct)).ShouldBe(ChannelSendResult.Permanent("device_inactive"));
        await _transport.DidNotReceiveWithAnyArgs().SendAsync(default!, Ct);
    }

    [Fact]
    public async Task A_device_registered_to_another_user_since_is_refused()
    {
        var device = Device();
        device.Reregister(Guid.NewGuid(), Guid.NewGuid(), PushPlatform.Android, null, null, Now);

        (await Channel().SendAsync(Work(device), Ct)).ShouldBe(ChannelSendResult.Permanent("device_reassigned"));
        device.IsActive.ShouldBeTrue();
        await _transport.DidNotReceiveWithAnyArgs().SendAsync(default!, Ct);
    }

    [Fact]
    public async Task An_ended_session_deactivates_the_device()
    {
        var device = Device(sessionActive: false);

        var result = await Channel().SendAsync(Work(device), Ct);

        result.ShouldBe(ChannelSendResult.Permanent("session_ended"));
        device.DeactivationReason.ShouldBe(PushDeviceDeactivationReason.SessionEnded);
        device.DeactivatedAt.ShouldBe(Now);
        await _transport.DidNotReceiveWithAnyArgs().SendAsync(default!, Ct);
    }

    [Fact]
    public async Task A_provider_rejection_that_deactivates_marks_the_device_RejectedByProvider()
    {
        var device = Device();
        _transport.SendAsync(Arg.Any<PushMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PushSendException(isPermanent: true, "fcm_unregistered", deactivateDevice: true));

        var result = await Channel().SendAsync(Work(device), Ct);

        result.ShouldBe(ChannelSendResult.Permanent("fcm_unregistered"));
        device.DeactivationReason.ShouldBe(PushDeviceDeactivationReason.RejectedByProvider);
    }

    [Fact]
    public async Task A_transient_failure_keeps_the_device()
    {
        var device = Device();
        _transport.SendAsync(Arg.Any<PushMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PushSendException(isPermanent: false, "fcm_unavailable", deactivateDevice: false));

        var result = await Channel().SendAsync(Work(device), Ct);

        result.ShouldBe(ChannelSendResult.Transient("fcm_unavailable"));
        device.IsActive.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Critical", true)]
    [InlineData("High", true)]
    [InlineData("Normal", false)]
    [InlineData("Low", false)]
    public async Task Urgent_follows_priority(string priority, bool urgent)
    {
        var device = Device();
        PushMessage? sent = null;
        await _transport.SendAsync(Arg.Do<PushMessage>(message => sent = message), Arg.Any<CancellationToken>());

        await Channel().SendAsync(Work(device) with { Priority = Enum.Parse<NotificationPriority>(priority) }, Ct);

        sent.ShouldNotBeNull().Urgent.ShouldBe(urgent);
    }

    [Fact]
    public async Task Title_and_body_are_cut()
    {
        var device = Device();
        PushMessage? sent = null;
        await _transport.SendAsync(Arg.Do<PushMessage>(message => sent = message), Arg.Any<CancellationToken>());

        await Channel().SendAsync(Work(device) with { Message = new RenderedMessage(new string('t', 250), new string('b', 1200), null) }, Ct);

        sent.ShouldNotBeNull().Title.Length.ShouldBe(200);
        sent.Body.Length.ShouldBe(1000);
    }

    [Fact]
    public void The_message_never_prints_the_token_title_or_body()
    {
        var text = new PushMessage(Guid.NewGuid(), Guid.NewGuid(), Token, "Secret title", "Secret body", new Dictionary<string, string>(), Urgent: true).ToString();

        text.ShouldNotContain(Token);
        text.ShouldNotContain("Secret");
    }

    private PushDevice Device(bool sessionActive = true)
    {
        var device = PushDevice.Register(UserId, Guid.NewGuid(), PushPlatform.Android, Token, null, null, Now.AddDays(-1));
        _devices.GetAsync(device.Id, Arg.Any<CancellationToken>()).Returns(device);
        _sessions.GetActiveSessionIdsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => sessionActive
                ? call.Arg<IReadOnlyCollection<Guid>>().Where(id => id == device.SessionId && call.Arg<Guid>() == UserId).ToHashSet()
                : new HashSet<Guid>());
        return device;
    }

    private static DeliveryWork Work(PushDevice device, string? destination = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            UserId,
            "auth.token_reuse_detected",
            NotificationCategory.Security,
            destination ?? PushDestination.For(device.Id),
            new RenderedMessage("Security alert", "Someone used an old session.", null),
            NotificationPriority.Critical);

    private PushChannel Channel() => new(_devices, _sessions, _transport, new FakeTimeProvider(Now));
}
