using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common;
using TemplateName.Infrastructure.Common.Idempotency;

namespace TemplateName.UnitTests.Infrastructure;

public sealed class IdempotencyOptionsTests
{
    [Fact]
    public void Time_to_live_defaults_to_one_day()
    {
        using var services = BuildServices(new Dictionary<string, string?>());

        services.GetRequiredService<IOptions<IdempotencyOptions>>().Value.TimeToLive.ShouldBe(TimeSpan.FromDays(1));
    }

    [Fact]
    public void Zero_time_to_live_fails_validation()
    {
        using var services = BuildServices(new Dictionary<string, string?> { ["Idempotency:TimeToLive"] = "00:00:00" });

        var exception = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IOptions<IdempotencyOptions>>().Value);

        exception.OptionsType.ShouldBe(typeof(IdempotencyOptions));
    }

    [Fact]
    public void In_progress_timeout_defaults_to_five_minutes()
    {
        using var services = BuildServices(new Dictionary<string, string?>());

        services.GetRequiredService<IOptions<IdempotencyOptions>>().Value.InProgressTimeout.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Theory]
    [InlineData("00:00:04")]
    [InlineData("01:00:01")]
    public void In_progress_timeout_out_of_range_fails_validation(string value)
    {
        using var services = BuildServices(new Dictionary<string, string?> { ["Idempotency:InProgressTimeout"] = value });

        var exception = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IOptions<IdempotencyOptions>>().Value);

        exception.OptionsType.ShouldBe(typeof(IdempotencyOptions));
    }

    [Fact]
    public void In_progress_timeout_longer_than_time_to_live_fails_validation()
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            ["Idempotency:TimeToLive"] = "00:02:00",
            ["Idempotency:InProgressTimeout"] = "00:03:00",
        });

        var exception = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IOptions<IdempotencyOptions>>().Value);

        exception.Message.ShouldContain(nameof(IdempotencyOptions.InProgressTimeout));
    }

    [Fact]
    public void In_progress_timeout_within_range_and_time_to_live_is_valid()
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            ["Idempotency:TimeToLive"] = "00:02:00",
            ["Idempotency:InProgressTimeout"] = "00:00:05",
        });

        services.GetRequiredService<IOptions<IdempotencyOptions>>().Value.InProgressTimeout.ShouldBe(TimeSpan.FromSeconds(5));
    }

    private static ServiceProvider BuildServices(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddLogging().AddInfrastructureCommon(configuration).BuildServiceProvider();
    }
}
