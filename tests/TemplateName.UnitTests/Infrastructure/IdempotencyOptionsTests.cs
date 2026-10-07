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

    private static ServiceProvider BuildServices(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddLogging().AddInfrastructureCommon(configuration).BuildServiceProvider();
    }
}
