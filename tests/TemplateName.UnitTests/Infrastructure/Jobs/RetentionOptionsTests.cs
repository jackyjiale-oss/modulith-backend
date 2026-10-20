using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common;
using TemplateName.Infrastructure.Common.Persistence;

namespace TemplateName.UnitTests.Infrastructure.Jobs;

public sealed class RetentionOptionsTests
{
    [Fact]
    public void Retention_defaults_match_the_spec()
    {
        using var services = BuildServices(new Dictionary<string, string?>());

        var options = services.GetRequiredService<IOptions<RetentionOptions>>().Value;

        options.BatchSize.ShouldBe(1000);
        options.Outbox.ShouldBe(TimeSpan.FromDays(7));
        options.Inbox.ShouldBe(TimeSpan.FromDays(7));
    }

    [Theory]
    [InlineData("23:00:00")]
    [InlineData("00:00:01")]
    public void Inbox_retention_below_one_day_is_rejected(string value)
    {
        using var services = BuildServices(new Dictionary<string, string?> { ["Retention:Inbox"] = value });

        var exception = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IOptions<RetentionOptions>>().Value);

        exception.OptionsType.ShouldBe(typeof(RetentionOptions));
        exception.Message.ShouldContain(nameof(RetentionOptions.Inbox));
    }

    [Fact]
    public void Inbox_retention_of_exactly_one_day_is_allowed()
    {
        using var services = BuildServices(new Dictionary<string, string?> { ["Retention:Inbox"] = "1.00:00:00" });

        services.GetRequiredService<IOptions<RetentionOptions>>().Value.Inbox.ShouldBe(TimeSpan.FromDays(1));
    }

    [Theory]
    [InlineData("Retention:Inbox", "00:00:00")]
    [InlineData("Retention:Inbox", "")]
    [InlineData("Retention:Outbox", "00:00:00")]
    [InlineData("Retention:Outbox", "")]
    public void Inbox_retention_of_zero_or_null_is_allowed_meaning_off(string key, string value)
    {
        using var services = BuildServices(new Dictionary<string, string?> { [key] = value });

        var options = services.GetRequiredService<IOptions<RetentionOptions>>().Value;

        var retention = key == "Retention:Inbox" ? options.Inbox : options.Outbox;
        (retention is null || retention == TimeSpan.Zero).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Retention:Outbox", "-1.00:00:00")]
    [InlineData("Retention:Inbox", "-1.00:00:00")]
    [InlineData("Retention:Outbox", "-00:00:01")]
    public void Negative_retention_is_rejected(string key, string value)
    {
        using var services = BuildServices(new Dictionary<string, string?> { [key] = value });

        var exception = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IOptions<RetentionOptions>>().Value);

        exception.OptionsType.ShouldBe(typeof(RetentionOptions));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("10001")]
    public void BatchSize_range_is_enforced(string value)
    {
        using var services = BuildServices(new Dictionary<string, string?> { ["Retention:BatchSize"] = value });

        var exception = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IOptions<RetentionOptions>>().Value);

        exception.OptionsType.ShouldBe(typeof(RetentionOptions));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("10000", 10000)]
    public void BatchSize_at_the_range_limits_is_accepted(string value, int expected)
    {
        using var services = BuildServices(new Dictionary<string, string?> { ["Retention:BatchSize"] = value });

        services.GetRequiredService<IOptions<RetentionOptions>>().Value.BatchSize.ShouldBe(expected);
    }

    [Theory]
    [InlineData("1.00:00:00", "5")]
    [InlineData("1.00:00:00", "28")]
    [InlineData("7.00:00:00", "100")]
    [InlineData("00:00:00", "100")]
    [InlineData("", "100")]
    public void Inbox_retention_that_outlasts_the_outbox_retries_is_allowed(string inbox, string maxAttempts)
    {
        using var services = BuildServices(new Dictionary<string, string?> { ["Retention:Inbox"] = inbox, ["Outbox:MaxAttempts"] = maxAttempts });

        var retention = services.GetRequiredService<IOptions<RetentionOptions>>().Value.Inbox;

        (retention is null || retention == TimeSpan.Zero || retention >= RetentionOptions.MinimumInbox).ShouldBeTrue();
    }

    [Theory]
    [InlineData("1.00:00:00", "29")]
    [InlineData("1.00:00:00", "100")]
    [InlineData("3.00:00:00", "100")]
    public void Inbox_retention_that_the_outbox_retries_outlast_is_rejected(string inbox, string maxAttempts)
    {
        using var services = BuildServices(new Dictionary<string, string?> { ["Retention:Inbox"] = inbox, ["Outbox:MaxAttempts"] = maxAttempts });

        var exception = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IOptions<RetentionOptions>>().Value);

        exception.OptionsType.ShouldBe(typeof(RetentionOptions));
        exception.Message.ShouldContain("Outbox:MaxAttempts");
        exception.Message.ShouldContain("Retention:Inbox");
    }

    private static ServiceProvider BuildServices(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddLogging().AddInfrastructureCommon(configuration).BuildServiceProvider();
    }
}
