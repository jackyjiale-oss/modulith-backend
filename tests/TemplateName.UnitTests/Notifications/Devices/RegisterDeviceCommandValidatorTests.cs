using TemplateName.Modules.Notifications.Application.Devices.Register;
using TemplateName.Modules.Notifications.Domain.PushDevices;

namespace TemplateName.UnitTests.Notifications.Devices;

public sealed class RegisterDeviceCommandValidatorTests
{
    private readonly RegisterDeviceCommandValidator _sut = new();

    private static RegisterDeviceCommand Command(
        string token = "fcm-token",
        PushPlatform platform = PushPlatform.Android,
        string? deviceName = null,
        string? appVersion = null) => new(platform, token, deviceName, appVersion);

    [Fact]
    public void A_valid_command_passes() => _sut.Validate(Command(deviceName: "Pixel", appVersion: "1.0")).IsValid.ShouldBeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_token_is_refused(string token) => _sut.Validate(Command(token)).IsValid.ShouldBeFalse();

    [Fact]
    public void Null_token_is_refused() => _sut.Validate(Command(null!)).IsValid.ShouldBeFalse();

    [Fact]
    public void Token_longer_than_4096_is_refused()
    {
        _sut.Validate(Command(new string('t', PushDevice.MaxTokenLength))).IsValid.ShouldBeTrue();
        _sut.Validate(Command(new string('t', PushDevice.MaxTokenLength + 1))).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("abc\u0000")]
    [InlineData("a b")]
    [InlineData("abc\n")]
    [InlineData("\tabc")]
    public void Token_with_control_characters_is_refused(string token)
    {
        var result = _sut.Validate(Command(token));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldAllBe(error => !error.ErrorMessage.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public void Undefined_platform_is_refused() => _sut.Validate(Command(platform: (PushPlatform)9)).IsValid.ShouldBeFalse();

    [Fact]
    public void Device_name_over_100_is_refused()
    {
        _sut.Validate(Command(deviceName: new string('n', 100))).IsValid.ShouldBeTrue();
        _sut.Validate(Command(deviceName: new string('n', 101))).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void App_version_over_50_is_refused()
    {
        _sut.Validate(Command(appVersion: new string('v', 50))).IsValid.ShouldBeTrue();
        _sut.Validate(Command(appVersion: new string('v', 51))).IsValid.ShouldBeFalse();
    }
}
