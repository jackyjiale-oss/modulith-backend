using FluentValidation.TestHelper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth;
using TemplateName.Modules.Auth.Application.Phone;
using TemplateName.Modules.Auth.Application.Phone.Confirm;
using TemplateName.Modules.Auth.Application.Phone.RequestCode;

namespace TemplateName.UnitTests.Auth.Phone;

public sealed class PhoneOptionsTests
{
    private const string Section = "Auth:Phone";

    [Fact]
    public void Defaults_match_the_spec()
    {
        using var services = BuildServices(new Dictionary<string, string?>());

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
        var options = services.GetRequiredService<IOptions<PhoneOptions>>().Value;
        PhoneOptions.SectionName.ShouldBe(Section);
        options.Enabled.ShouldBeFalse();
        options.AllowedRegions.ShouldBeEmpty();
        options.CodeLifetime.ShouldBe(TimeSpan.FromMinutes(10));
        options.ResendCooldown.ShouldBe(TimeSpan.FromMinutes(1));
        options.MaxCodesPerDay.ShouldBe(5);
        options.MaxAttempts.ShouldBe(5);
        options.SignInEnabled.ShouldBeFalse();
        options.SignInCodeLifetime.ShouldBe(TimeSpan.FromMinutes(5));
        options.DeniedRoleSet().ShouldBe(["SUPERADMIN", "ADMIN"], ignoreOrder: true);
    }

    [Fact]
    public void Configured_denied_roles_replace_the_defaults()
    {
        using var services = BuildServices(new Dictionary<string, string?> { [$"{Section}:SignInDeniedRoles:0"] = "Support" });

        services.GetRequiredService<IOptions<PhoneOptions>>().Value.DeniedRoleSet().ShouldBe(["SUPPORT"]);
    }

    [Fact]
    public void Sign_in_without_phone_enabled_is_refused()
    {
        using var services = BuildServices(new Dictionary<string, string?> { [$"{Section}:SignInEnabled"] = "true" });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldContain("SignInEnabled");
        failure.Message.ShouldContain("Enabled");
    }

    [Fact]
    public void Sign_in_with_phone_enabled_starts()
    {
        using var services = BuildServices(new Dictionary<string, string?> { [$"{Section}:Enabled"] = "true", [$"{Section}:SignInEnabled"] = "true" });

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_role_is_refused(string role)
    {
        using var services = BuildServices(new Dictionary<string, string?> { [$"{Section}:SignInDeniedRoles:0"] = "Admin", [$"{Section}:SignInDeniedRoles:1"] = role });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldContain("SignInDeniedRoles");
    }

    [Theory]
    [InlineData("CodeLifetime", "00:01:00")]
    [InlineData("CodeLifetime", "00:30:00")]
    [InlineData("ResendCooldown", "00:00:00")]
    [InlineData("ResendCooldown", "00:10:00")]
    [InlineData("MaxCodesPerDay", "1")]
    [InlineData("MaxCodesPerDay", "50")]
    [InlineData("MaxAttempts", "1")]
    [InlineData("MaxAttempts", "10")]
    [InlineData("AllowedRegions:0", "MY")]
    [InlineData("SignInCodeLifetime", "00:01:00")]
    [InlineData("SignInCodeLifetime", "00:10:00")]
    public void Values_at_the_ends_of_their_ranges_start(string key, string value)
    {
        using var services = BuildServices(new Dictionary<string, string?> { [$"{Section}:{key}"] = value });

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
    }

    [Theory]
    [InlineData("CodeLifetime", "00:00:59")]
    [InlineData("CodeLifetime", "00:30:01")]
    [InlineData("ResendCooldown", "-00:00:01")]
    [InlineData("ResendCooldown", "00:10:01")]
    [InlineData("MaxCodesPerDay", "0")]
    [InlineData("MaxCodesPerDay", "51")]
    [InlineData("MaxAttempts", "0")]
    [InlineData("MaxAttempts", "11")]
    [InlineData("SignInCodeLifetime", "00:00:59")]
    [InlineData("SignInCodeLifetime", "00:10:01")]
    public void Values_outside_their_ranges_stop_the_host(string key, string value)
    {
        using var services = BuildServices(new Dictionary<string, string?> { [$"{Section}:{key}"] = value });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.OptionsType.ShouldBe(typeof(PhoneOptions));
        failure.Message.ShouldContain(key);
    }

    [Theory]
    [InlineData("ZZ")]
    [InlineData("my")]
    [InlineData("")]
    public void An_unknown_region_stops_the_host_naming_the_key(string region)
    {
        using var services = BuildServices(new Dictionary<string, string?> { [$"{Section}:AllowedRegions:0"] = "MY", [$"{Section}:AllowedRegions:1"] = region });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldContain("AllowedRegions");
    }

    [Theory]
    [InlineData("+60123456789")]
    [InlineData("+60 12-345 6789")]
    public void A_number_up_to_32_characters_passes_validation(string number) =>
        new RequestPhoneCodeCommandValidator().TestValidate(new RequestPhoneCodeCommand(number)).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+601234567890123456789012345678901")]
    public void A_blank_or_long_number_fails_validation(string number) =>
        new RequestPhoneCodeCommandValidator().TestValidate(new RequestPhoneCodeCommand(number)).ShouldHaveValidationErrorFor(command => command.PhoneNumber);

    [Fact]
    public void A_six_digit_code_passes_validation() =>
        new ConfirmPhoneCommandValidator().TestValidate(new ConfirmPhoneCommand("042137")).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData("１２３４５６")]
    public void A_code_that_is_not_six_ascii_digits_fails_validation(string code) =>
        new ConfirmPhoneCommandValidator().TestValidate(new ConfirmPhoneCommand(code)).ShouldHaveValidationErrorFor(command => command.Code);

    private static ServiceProvider BuildServices(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        AuthModule.AddPhone(services, configuration);
        return services.BuildServiceProvider();
    }
}
