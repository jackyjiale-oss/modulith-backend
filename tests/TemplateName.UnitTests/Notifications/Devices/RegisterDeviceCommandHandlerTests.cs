using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Devices;
using TemplateName.Modules.Notifications.Application.Devices.Register;
using TemplateName.Modules.Notifications.Domain.PushDevices;

namespace TemplateName.UnitTests.Notifications.Devices;

public sealed class RegisterDeviceCommandHandlerTests
{
    private const string Token = "fcm-token-1";

    private static readonly DateTimeOffset Now = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);

    private static readonly Guid UserId = Guid.NewGuid();

    private static readonly Guid SessionId = Guid.NewGuid();

    private readonly IPushDeviceRepository _devices = Substitute.For<IPushDeviceRepository>();

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    private readonly PushOptions _options = new();

    public RegisterDeviceCommandHandlerTests()
    {
        _currentUser.UserId.Returns(UserId);
        _currentUser.SessionId.Returns(SessionId);
        _devices.ListActiveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
        _unitOfWork.SaveChangesUnlessConflictAsync(Arg.Any<CancellationToken>()).Returns(true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_token_adds_a_device_bound_to_the_session()
    {
        PushDevice? added = null;
        _devices.Add(Arg.Do<PushDevice>(device => added = device));

        var result = await Handler().HandleAsync(new RegisterDeviceCommand(PushPlatform.Ios, Token, "iPhone", "3.1"), Ct);

        result.IsSuccess.ShouldBeTrue();
        added.ShouldNotBeNull();
        added.UserId.ShouldBe(UserId);
        added.SessionId.ShouldBe(SessionId);
        added.Platform.ShouldBe(PushPlatform.Ios);
        added.Token.ShouldBe(Token);
        result.Value.ShouldBe(new DeviceResponse(added.Id, PushPlatform.Ios, "iPhone", "3.1", Now));
        await _unitOfWork.Received(1).SaveChangesUnlessConflictAsync(Ct);
    }

    [Fact]
    public async Task A_known_token_moves_to_the_caller()
    {
        var existing = PushDevice.Register(Guid.NewGuid(), Guid.NewGuid(), PushPlatform.Android, Token, "Old phone", null, Now.AddDays(-9));
        _devices.FindByTokenHashAsync(Arg.Is<byte[]>(hash => hash.SequenceEqual(PushDevice.HashOf(Token))), Arg.Any<CancellationToken>())
            .Returns(existing);

        var result = await Handler().HandleAsync(new RegisterDeviceCommand(PushPlatform.Android, Token, "My phone", null), Ct);

        result.IsSuccess.ShouldBeTrue();
        existing.UserId.ShouldBe(UserId);
        existing.SessionId.ShouldBe(SessionId);
        existing.DeviceName.ShouldBe("My phone");
        existing.LastRegisteredAt.ShouldBe(Now);
        result.Value.Id.ShouldBe(existing.Id);
        _devices.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Fact]
    public async Task Re_registering_in_a_new_session_moves_the_device()
    {
        var existing = PushDevice.Register(UserId, Guid.NewGuid(), PushPlatform.Android, Token, null, null, Now.AddDays(-1));
        existing.Deactivate(PushDeviceDeactivationReason.SessionEnded, Now.AddHours(-1));
        _devices.FindByTokenHashAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(existing);

        var result = await Handler().HandleAsync(new RegisterDeviceCommand(PushPlatform.Android, Token, null, null), Ct);

        result.IsSuccess.ShouldBeTrue();
        existing.SessionId.ShouldBe(SessionId);
        existing.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task Registering_beyond_the_cap_deactivates_the_oldest_with_Replaced()
    {
        _options.MaxDevicesPerUser = 2;
        var newer = PushDevice.Register(UserId, Guid.NewGuid(), PushPlatform.Android, "newer", null, null, Now.AddHours(-1));
        var older = PushDevice.Register(UserId, Guid.NewGuid(), PushPlatform.Android, "older", null, null, Now.AddHours(-2));
        _devices.ListActiveAsync(UserId, Arg.Any<CancellationToken>()).Returns([newer, older]);

        var result = await Handler().HandleAsync(new RegisterDeviceCommand(PushPlatform.Android, Token, null, null), Ct);

        result.IsSuccess.ShouldBeTrue();
        older.IsActive.ShouldBeFalse();
        older.DeactivationReason.ShouldBe(PushDeviceDeactivationReason.Replaced);
        older.DeactivatedAt.ShouldBe(Now);
        newer.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task Re_registering_a_listed_device_does_not_count_it_twice()
    {
        _options.MaxDevicesPerUser = 2;
        var existing = PushDevice.Register(UserId, Guid.NewGuid(), PushPlatform.Android, Token, null, null, Now.AddHours(-3));
        var other = PushDevice.Register(UserId, Guid.NewGuid(), PushPlatform.Android, "other", null, null, Now.AddHours(-1));
        _devices.FindByTokenHashAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(existing);
        _devices.ListActiveAsync(UserId, Arg.Any<CancellationToken>()).Returns([other, existing]);

        (await Handler().HandleAsync(new RegisterDeviceCommand(PushPlatform.Android, Token, null, null), Ct)).IsSuccess.ShouldBeTrue();

        existing.IsActive.ShouldBeTrue();
        other.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task A_conflict_on_save_stages_the_registration_again()
    {
        _unitOfWork.SaveChangesUnlessConflictAsync(Arg.Any<CancellationToken>()).Returns(false, true);

        var result = await Handler().HandleAsync(new RegisterDeviceCommand(PushPlatform.Web, Token, null, null), Ct);

        result.IsSuccess.ShouldBeTrue();
        await _devices.Received(2).FindByTokenHashAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(2).SaveChangesUnlessConflictAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_a_session_the_caller_is_unauthenticated()
    {
        _currentUser.SessionId.Returns((Guid?)null);

        var result = await Handler().HandleAsync(new RegisterDeviceCommand(PushPlatform.Android, Token, null, null), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("http.401");
        _devices.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Fact]
    public void The_command_never_prints_the_token()
        => new RegisterDeviceCommand(PushPlatform.Android, Token, null, null).ToString().ShouldNotContain(Token);

    [Fact]
    public void The_response_has_no_token() => typeof(DeviceResponse).GetProperty("Token").ShouldBeNull();

    private RegisterDeviceCommandHandler Handler()
        => new(_devices, _unitOfWork, _currentUser, new FakeTimeProvider(Now), Options.Create(_options));
}
