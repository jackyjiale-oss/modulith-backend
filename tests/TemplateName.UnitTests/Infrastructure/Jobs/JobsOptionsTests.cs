using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common;
using TemplateName.Infrastructure.Common.Jobs;

namespace TemplateName.UnitTests.Infrastructure.Jobs;

public sealed class JobsOptionsTests
{
    [Fact]
    public void Defaults_match_the_spec()
    {
        using var services = BuildServices(new Dictionary<string, string?>());

        var options = services.GetRequiredService<IOptions<JobsOptions>>().Value;

        options.Enabled.ShouldBeTrue();
        options.TickInterval.ShouldBe(TimeSpan.FromSeconds(30));
        options.LeaseDuration.ShouldBe(TimeSpan.FromMinutes(15));
        options.MaxBatchesPerRun.ShouldBe(100);
    }

    [Theory]
    [InlineData("Jobs:TickInterval", "00:00:01")]
    [InlineData("Jobs:TickInterval", "01:00:01")]
    [InlineData("Jobs:LeaseDuration", "00:00:10")]
    [InlineData("Jobs:LeaseDuration", "1.00:00:01")]
    [InlineData("Jobs:MaxBatchesPerRun", "0")]
    [InlineData("Jobs:MaxBatchesPerRun", "10001")]
    public void Out_of_range_values_are_rejected(string key, string value)
    {
        using var services = BuildServices(new Dictionary<string, string?> { [key] = value });

        var exception = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IOptions<JobsOptions>>().Value);

        exception.OptionsType.ShouldBe(typeof(JobsOptions));
    }

    [Fact]
    public void Values_at_the_range_limits_are_accepted()
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            ["Jobs:TickInterval"] = "00:00:05",
            ["Jobs:LeaseDuration"] = "1.00:00:00",
            ["Jobs:MaxBatchesPerRun"] = "10000",
        });

        var options = services.GetRequiredService<IOptions<JobsOptions>>().Value;

        options.TickInterval.ShouldBe(TimeSpan.FromSeconds(5));
        options.LeaseDuration.ShouldBe(TimeSpan.FromDays(1));
        options.MaxBatchesPerRun.ShouldBe(10000);
    }

    [Fact]
    public void Job_interval_and_enabled_come_from_its_section_else_the_default()
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            ["Jobs:ConfiguredCleanup:Enabled"] = "false",
            ["Jobs:ConfiguredCleanup:Interval"] = "00:10:00",
        });
        var jobOptions = services.GetRequiredService<IOptionsMonitor<RecurringJobOptions>>();

        var configured = jobOptions.Get("ConfiguredCleanup");
        configured.Enabled.ShouldBeFalse();
        configured.IntervalOr(TimeSpan.FromDays(1)).ShouldBe(TimeSpan.FromMinutes(10));

        var unconfigured = jobOptions.Get("UnconfiguredCleanup");
        unconfigured.Enabled.ShouldBeTrue();
        unconfigured.IntervalOr(TimeSpan.FromDays(1)).ShouldBe(TimeSpan.FromDays(1));
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:01:00")]
    [InlineData("366.00:00:00")]
    public void A_job_interval_out_of_range_is_rejected(string value)
    {
        using var services = BuildServices(new Dictionary<string, string?> { ["Jobs:ConfiguredCleanup:Interval"] = value });
        var jobOptions = services.GetRequiredService<IOptionsMonitor<RecurringJobOptions>>();

        var exception = Should.Throw<OptionsValidationException>(() => jobOptions.Get("ConfiguredCleanup"));

        exception.OptionsName.ShouldBe("ConfiguredCleanup");
    }

    [Fact]
    public void Unknown_job_sections_are_ignored()
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            ["Jobs:NoSuchCleanup:Enabled"] = "not a boolean",
            ["Jobs:NoSuchCleanup:Interval"] = "not a time span",
        });

        var options = services.GetRequiredService<IOptions<JobsOptions>>().Value;
        options.Enabled.ShouldBeTrue();
        options.TickInterval.ShouldBe(TimeSpan.FromSeconds(30));

        var other = services.GetRequiredService<IOptionsMonitor<RecurringJobOptions>>().Get("OtherCleanup");
        other.Enabled.ShouldBeTrue();
        other.IntervalOr(TimeSpan.FromHours(1)).ShouldBe(TimeSpan.FromHours(1));
    }

    private static ServiceProvider BuildServices(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddLogging().AddInfrastructureCommon(configuration).BuildServiceProvider();
    }
}
