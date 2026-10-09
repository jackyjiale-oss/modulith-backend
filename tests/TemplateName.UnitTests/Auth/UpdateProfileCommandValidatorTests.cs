using FluentValidation.TestHelper;
using TemplateName.Modules.Auth.Application.Me.Update;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.UnitTests.Auth;

public sealed class UpdateProfileCommandValidatorTests
{
    private readonly UpdateProfileCommandValidator _sut = new();

    [Fact]
    public void Valid_command_passes()
    {
        _sut.TestValidate(ValidCommand()).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_display_name_is_rejected(string? displayName)
    {
        _sut.TestValidate(ValidCommand() with { DisplayName = displayName! }).ShouldHaveValidationErrorFor(command => command.DisplayName);
    }

    [Fact]
    public void Display_name_up_to_the_column_limit_is_accepted()
    {
        _sut.TestValidate(ValidCommand() with { DisplayName = new string('n', User.MaxDisplayNameLength) })
            .ShouldNotHaveValidationErrorFor(command => command.DisplayName);
    }

    [Fact]
    public void Display_name_over_the_column_limit_is_rejected()
    {
        _sut.TestValidate(ValidCommand() with { DisplayName = new string('n', User.MaxDisplayNameLength + 1) })
            .ShouldHaveValidationErrorFor(command => command.DisplayName);
    }

    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("Not A Zone")]
    [InlineData("Pacific Standard Time")]
    [InlineData("../../etc/passwd")]
    [InlineData(" UTC")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Invalid_time_zone_is_rejected(string? timeZone)
    {
        _sut.TestValidate(ValidCommand() with { TimeZone = timeZone! }).ShouldHaveValidationErrorFor(command => command.TimeZone);
    }

    [Fact]
    public void Time_zone_longer_than_its_column_is_rejected()
    {
        _sut.TestValidate(ValidCommand() with { TimeZone = new string('a', 65) }).ShouldHaveValidationErrorFor(command => command.TimeZone);
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("Asia/Kuala_Lumpur")]
    [InlineData("Europe/London")]
    [InlineData("America/New_York")]
    public void Iana_time_zone_ids_are_accepted(string timeZone)
    {
        _sut.TestValidate(ValidCommand() with { TimeZone = timeZone }).ShouldNotHaveValidationErrorFor(command => command.TimeZone);
    }

    [Theory]
    [InlineData("xx-invalid")]
    [InlineData("klingon")]
    [InlineData("en_")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("an-extremely-long-culture-name")]
    public void Locale_must_be_a_known_culture(string? locale)
    {
        _sut.TestValidate(ValidCommand() with { Locale = locale! }).ShouldHaveValidationErrorFor(command => command.Locale);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ms")]
    [InlineData("zh-Hans")]
    [InlineData("ms-MY")]
    [InlineData("MS")]
    public void Known_cultures_are_accepted_in_any_case(string locale)
    {
        _sut.TestValidate(ValidCommand() with { Locale = locale }).ShouldNotHaveValidationErrorFor(command => command.Locale);
    }

    private static UpdateProfileCommand ValidCommand() => new("Alice Liddell", "ms", "Asia/Kuala_Lumpur");
}
