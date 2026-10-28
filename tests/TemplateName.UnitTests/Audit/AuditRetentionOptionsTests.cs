using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Audit;
using TemplateName.Modules.Audit.Infrastructure.Retention;

namespace TemplateName.UnitTests.Audit;

public sealed class AuditRetentionOptionsTests
{
    [Fact]
    public void Trails_is_null_by_default_and_the_section_is_Audit_Retention()
    {
        using var services = BuildServices(new Dictionary<string, string?>());

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
        AuditRetentionOptions.SectionName.ShouldBe("Audit:Retention");
        services.GetRequiredService<IOptions<AuditRetentionOptions>>().Value.Trails.ShouldBeNull();
    }

    [Fact]
    public void A_negative_retention_is_rejected_when_the_host_starts()
    {
        using var services = BuildServices(new Dictionary<string, string?> { ["Audit:Retention:Trails"] = "-1.00:00:00" });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.OptionsType.ShouldBe(typeof(AuditRetentionOptions));
        failure.Message.ShouldContain("Trails");
    }

    [Fact]
    public void Zero_and_null_turn_the_cleanup_off_and_are_accepted()
    {
        using var zero = BuildServices(new Dictionary<string, string?> { ["Audit:Retention:Trails"] = "00:00:00" });
        using var blank = BuildServices(new Dictionary<string, string?> { ["Audit:Retention:Trails"] = string.Empty });

        Should.NotThrow(() => zero.GetRequiredService<IStartupValidator>().Validate());
        Should.NotThrow(() => blank.GetRequiredService<IStartupValidator>().Validate());
        zero.GetRequiredService<IOptions<AuditRetentionOptions>>().Value.Trails.ShouldBe(TimeSpan.Zero);
        blank.GetRequiredService<IOptions<AuditRetentionOptions>>().Value.Trails.ShouldBeNull();
    }

    [Fact]
    public void Trails_can_be_given_a_retention()
    {
        using var services = BuildServices(new Dictionary<string, string?> { ["Audit:Retention:Trails"] = "365.00:00:00" });

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
        services.GetRequiredService<IOptions<AuditRetentionOptions>>().Value.Trails.ShouldBe(TimeSpan.FromDays(365));
    }

    private static ServiceProvider BuildServices(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        AuditModule.AddRetention(services, configuration);
        return services.BuildServiceProvider();
    }
}
