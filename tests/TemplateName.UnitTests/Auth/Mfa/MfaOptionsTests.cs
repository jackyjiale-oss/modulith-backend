using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth;
using TemplateName.Modules.Auth.Application.Mfa;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class MfaOptionsTests
{
    private const string Section = "Auth:Mfa";

    [Fact]
    public void Defaults_match_the_plan()
    {
        using var services = BuildServices(new Dictionary<string, string?>());

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
        var options = services.GetRequiredService<IOptions<MfaOptions>>().Value;
        MfaOptions.SectionName.ShouldBe(Section);
        options.Issuer.ShouldBe("TemplateName");
        options.ChallengeLifetime.ShouldBe(TimeSpan.FromMinutes(5));
        options.MaxChallengeAttempts.ShouldBe(5);
        options.TrustedDevicesEnabled.ShouldBeTrue();
        options.TrustedDeviceLifetime.ShouldBe(TimeSpan.FromDays(30));
    }

    [Theory]
    [InlineData("ChallengeLifetime", "00:01:00")]
    [InlineData("ChallengeLifetime", "00:15:00")]
    [InlineData("MaxChallengeAttempts", "1")]
    [InlineData("MaxChallengeAttempts", "10")]
    [InlineData("TrustedDeviceLifetime", "1.00:00:00")]
    [InlineData("TrustedDeviceLifetime", "365.00:00:00")]
    [InlineData("TrustedDevicesEnabled", "false")]
    [InlineData("Issuer", "A")]
    [InlineData("Issuer", "Acme HR (staging)")]
    public void Values_at_the_ends_of_their_ranges_start(string key, string value)
    {
        using var services = BuildServices(new Dictionary<string, string?> { [$"{Section}:{key}"] = value });

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
    }

    [Theory]
    [InlineData("ChallengeLifetime", "00:00:59")]
    [InlineData("ChallengeLifetime", "00:15:01")]
    [InlineData("MaxChallengeAttempts", "0")]
    [InlineData("MaxChallengeAttempts", "11")]
    [InlineData("TrustedDeviceLifetime", "23:59:59")]
    [InlineData("TrustedDeviceLifetime", "365.00:00:01")]
    [InlineData("Issuer", "")]
    [InlineData("Issuer", "   ")]
    public void Values_outside_their_ranges_fail_on_start(string key, string value)
    {
        using var services = BuildServices(new Dictionary<string, string?> { [$"{Section}:{key}"] = value });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.OptionsType.ShouldBe(typeof(MfaOptions));
        failure.Message.ShouldContain(key);
    }

    [Fact]
    public void Issuer_of_64_characters_starts_and_of_65_fails_on_start()
    {
        using var longest = BuildServices(new Dictionary<string, string?> { [$"{Section}:Issuer"] = new string('a', 64) });
        using var tooLong = BuildServices(new Dictionary<string, string?> { [$"{Section}:Issuer"] = new string('a', 65) });

        Should.NotThrow(() => longest.GetRequiredService<IStartupValidator>().Validate());
        Should.Throw<OptionsValidationException>(() => tooLong.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain("Issuer");
    }

    [Fact]
    public void Issuer_with_a_colon_fails_on_start()
    {
        // The issuer is the prefix of the otpauth label (issuer:account); a colon in it would read as a second separator.
        using var services = BuildServices(new Dictionary<string, string?> { [$"{Section}:Issuer"] = "Acme:HR" });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.OptionsType.ShouldBe(typeof(MfaOptions));
        failure.Message.ShouldContain("Issuer");
        failure.Message.ShouldContain("':'");
    }

    private static ServiceProvider BuildServices(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        AuthModule.AddMfa(services, configuration);
        return services.BuildServiceProvider();
    }
}
