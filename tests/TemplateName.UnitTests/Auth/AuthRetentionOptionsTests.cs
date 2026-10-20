using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth;
using TemplateName.Modules.Auth.Infrastructure.Retention;

namespace TemplateName.UnitTests.Auth;

public sealed class AuthRetentionOptionsTests
{
    [Fact]
    public void Defaults_match_the_spec()
    {
        using var services = BuildServices(new Dictionary<string, string?>());

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
        var options = services.GetRequiredService<IOptions<AuthRetentionOptions>>().Value;
        AuthRetentionOptions.SectionName.ShouldBe("Auth:Retention");
        options.VerificationCodes.ShouldBe(TimeSpan.FromDays(7));
        options.RefreshTokens.ShouldBe(TimeSpan.FromDays(30));
        options.Sessions.ShouldBe(TimeSpan.FromDays(90));
        options.MfaChallenges.ShouldBe(TimeSpan.FromDays(1));
        options.TrustedDevices.ShouldBe(TimeSpan.FromDays(30));
    }

    [Fact]
    public void AuditLogs_is_null_by_default()
    {
        using var services = BuildServices(new Dictionary<string, string?>());

        services.GetRequiredService<IOptions<AuthRetentionOptions>>().Value.AuditLogs.ShouldBeNull();
    }

    [Theory]
    [InlineData("VerificationCodes")]
    [InlineData("RefreshTokens")]
    [InlineData("Sessions")]
    [InlineData("MfaChallenges")]
    [InlineData("TrustedDevices")]
    [InlineData("AuditLogs")]
    public void Negative_retention_is_rejected(string key)
    {
        using var services = BuildServices(new Dictionary<string, string?> { [$"Auth:Retention:{key}"] = "-1.00:00:00" });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.OptionsType.ShouldBe(typeof(AuthRetentionOptions));
        failure.Message.ShouldContain(key);
    }

    [Theory]
    [InlineData("VerificationCodes")]
    [InlineData("RefreshTokens")]
    [InlineData("Sessions")]
    [InlineData("MfaChallenges")]
    [InlineData("TrustedDevices")]
    [InlineData("AuditLogs")]
    public void Zero_and_null_turn_a_cleanup_off_and_are_accepted(string key)
    {
        using var zero = BuildServices(new Dictionary<string, string?> { [$"Auth:Retention:{key}"] = "00:00:00" });
        using var blank = BuildServices(new Dictionary<string, string?> { [$"Auth:Retention:{key}"] = string.Empty });

        Should.NotThrow(() => zero.GetRequiredService<IStartupValidator>().Validate());
        Should.NotThrow(() => blank.GetRequiredService<IStartupValidator>().Validate());
        var zeroed = zero.GetRequiredService<IOptions<AuthRetentionOptions>>().Value;
        zeroed.GetType().GetProperty(key)!.GetValue(zeroed).ShouldBe(TimeSpan.Zero);
        var blanked = blank.GetRequiredService<IOptions<AuthRetentionOptions>>().Value;
        blanked.GetType().GetProperty(key)!.GetValue(blanked).ShouldBeNull();
    }

    [Fact]
    public void Audit_logs_can_be_given_a_retention()
    {
        using var services = BuildServices(new Dictionary<string, string?> { ["Auth:Retention:AuditLogs"] = "365.00:00:00" });

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
        services.GetRequiredService<IOptions<AuthRetentionOptions>>().Value.AuditLogs.ShouldBe(TimeSpan.FromDays(365));
    }

    private static ServiceProvider BuildServices(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        AuthModule.AddRetention(services, configuration);
        return services.BuildServiceProvider();
    }
}
