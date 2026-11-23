using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Devices.List;
using TemplateName.Modules.Notifications.Application.Devices.Unregister;
using TemplateName.Modules.Notifications.Domain.PushDevices;

namespace TemplateName.UnitTests.Notifications.Devices;

public sealed class UnregisterDeviceCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);

    private static readonly Guid UserId = Guid.NewGuid();

    private readonly IPushDeviceRepository _devices = Substitute.For<IPushDeviceRepository>();

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    public UnregisterDeviceCommandHandlerTests() => _currentUser.UserId.Returns(UserId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deactivates_with_RemovedByUser()
    {
        var device = Device(UserId);

        var result = await Handler().HandleAsync(new UnregisterDeviceCommand(device.Id), Ct);

        result.IsSuccess.ShouldBeTrue();
        device.DeactivationReason.ShouldBe(PushDeviceDeactivationReason.RemovedByUser);
        device.DeactivatedAt.ShouldBe(Now);
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Another_users_device_is_not_found()
    {
        var device = Device(Guid.NewGuid());

        var result = await Handler().HandleAsync(new UnregisterDeviceCommand(device.Id), Ct);

        result.Error.Code.ShouldBe("notifications.device_not_found");
        device.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task An_unknown_device_is_not_found()
        => (await Handler().HandleAsync(new UnregisterDeviceCommand(Guid.NewGuid()), Ct)).Error.Code.ShouldBe("notifications.device_not_found");

    [Fact]
    public async Task An_inactive_device_answers_success()
    {
        var device = Device(UserId);
        device.Deactivate(PushDeviceDeactivationReason.SessionEnded, Now.AddDays(-1));

        var result = await Handler().HandleAsync(new UnregisterDeviceCommand(device.Id), Ct);

        result.IsSuccess.ShouldBeTrue();
        device.DeactivationReason.ShouldBe(PushDeviceDeactivationReason.SessionEnded);
    }

    [Fact]
    public async Task List_maps_the_active_devices_newest_first()
    {
        var newer = PushDevice.Register(UserId, Guid.NewGuid(), PushPlatform.Ios, "a", "iPad", "2", Now);
        var older = PushDevice.Register(UserId, Guid.NewGuid(), PushPlatform.Web, "b", null, null, Now.AddDays(-1));
        _devices.ListActiveAsync(UserId, Arg.Any<CancellationToken>()).Returns([newer, older]);

        var result = await new ListDevicesQueryHandler(_devices, _currentUser).HandleAsync(new ListDevicesQuery(), Ct);

        result.Value.Items.Select(item => item.Id).ShouldBe([newer.Id, older.Id]);
        result.Value.Items[0].DeviceName.ShouldBe("iPad");
    }

    private PushDevice Device(Guid owner)
    {
        var device = PushDevice.Register(owner, Guid.NewGuid(), PushPlatform.Android, Guid.NewGuid().ToString("N"), null, null, Now.AddDays(-2));
        _devices.GetAsync(device.Id, Arg.Any<CancellationToken>()).Returns(device);
        return device;
    }

    private UnregisterDeviceCommandHandler Handler() => new(_devices, _unitOfWork, _currentUser, new FakeTimeProvider(Now));
}
