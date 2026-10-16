using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Infrastructure.Deliveries;

namespace TemplateName.UnitTests.Notifications;

public sealed class DeliveryOptionsTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        var options = new DeliveryOptions();

        Validate(options).ShouldBeEmpty();
        DeliveryOptions.SectionName.ShouldBe("Notifications:Delivery");
        options.Enabled.ShouldBeTrue();
        options.PollingInterval.ShouldBe(TimeSpan.FromSeconds(5));
        options.BatchSize.ShouldBe(10);
        options.MaxAttempts.ShouldBe(5);
        options.LeaseDuration.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Theory]
    [InlineData("00:00:01", 1, 1, "00:00:30")]
    [InlineData("01:00:00", 100, 10, "01:00:00")]
    public void Range_limits_are_accepted(string pollingInterval, int batchSize, int maxAttempts, string leaseDuration)
    {
        var options = new DeliveryOptions
        {
            PollingInterval = TimeSpan.Parse(pollingInterval, System.Globalization.CultureInfo.InvariantCulture),
            BatchSize = batchSize,
            MaxAttempts = maxAttempts,
            LeaseDuration = TimeSpan.Parse(leaseDuration, System.Globalization.CultureInfo.InvariantCulture),
        };

        Validate(options).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("00:00:00.999", 10, 5, "00:05:00")]
    [InlineData("01:00:01", 10, 5, "00:05:00")]
    [InlineData("00:00:05", 0, 5, "00:05:00")]
    [InlineData("00:00:05", 101, 5, "00:05:00")]
    [InlineData("00:00:05", 10, 0, "00:05:00")]
    [InlineData("00:00:05", 10, 11, "00:05:00")]
    [InlineData("00:00:05", 10, 5, "00:00:29")]
    [InlineData("00:00:05", 10, 5, "01:00:01")]
    public void Out_of_range_values_are_rejected(string pollingInterval, int batchSize, int maxAttempts, string leaseDuration)
    {
        var options = new DeliveryOptions
        {
            PollingInterval = TimeSpan.Parse(pollingInterval, System.Globalization.CultureInfo.InvariantCulture),
            BatchSize = batchSize,
            MaxAttempts = maxAttempts,
            LeaseDuration = TimeSpan.Parse(leaseDuration, System.Globalization.CultureInfo.InvariantCulture),
        };

        Validate(options).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Disabled_background_service_returns_at_once_without_polling()
    {
        // A scope factory that cannot create anything the dispatcher needs: a poll would throw.
        var options = Options.Create(new DeliveryOptions { Enabled = false });
        await using var services = new ServiceCollection().BuildServiceProvider();
        var dispatcher = new DeliveryDispatcher(
            services.GetRequiredService<IServiceScopeFactory>(),
            options,
            new FakeTimeProvider(),
            Substitute.For<INotificationsMetrics>(),
            NullLogger<DeliveryDispatcher>.Instance);
        using var sut = new DeliveryBackgroundService(dispatcher, options, new FakeTimeProvider(), NullLogger<DeliveryBackgroundService>.Instance);

        await sut.StartAsync(TestContext.Current.CancellationToken);

        // An enabled service would poll and then wait on the fake clock, which never moves, so it would not finish.
        await sut.ExecuteTask.ShouldNotBeNull().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        sut.ExecuteTask.IsCompletedSuccessfully.ShouldBeTrue();
        await sut.StopAsync(TestContext.Current.CancellationToken);
    }

    private static List<ValidationResult> Validate(DeliveryOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}
