using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TemplateName.Infrastructure.Common.Http;

namespace TemplateName.UnitTests.Infrastructure.Http;

/// <summary>
/// A service provider with one external HTTP client over a scripted handler and a <see cref="FakeTimeProvider"/>, which the pipelines
/// use for their timeouts, delays and breaker windows, plus the means to move that time while a call is in flight.
/// </summary>
internal sealed class ResilienceTestHost : IDisposable
{
    public const string ClientName = "fake";

    private static readonly TimeSpan RealTimeGuard = TimeSpan.FromSeconds(20);

    private ResilienceTestHost(ServiceProvider services, FakeTimeProvider time)
    {
        Services = services;
        Time = time;
    }

    public ServiceProvider Services { get; }

    public FakeTimeProvider Time { get; }

    public IHttpClientFactory ClientFactory => Services.GetRequiredService<IHttpClientFactory>();

    public HttpClient Client => ClientFactory.CreateClient(ClientName);

    public static ResilienceTestHost Create(
        string pipeline,
        ScriptedHttpMessageHandler handler,
        IReadOnlyDictionary<string, string?>? configuration = null,
        string clientName = ClientName)
    {
        var time = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(configuration ?? new Dictionary<string, string?>()).Build());
        services.AddExternalHttpClient(clientName, pipeline).ConfigurePrimaryHttpMessageHandler(() => handler);
        return new ResilienceTestHost(services.BuildServiceProvider(), time);
    }

    /// <summary>Moves the fake time by <paramref name="total"/> in steps, giving the pending work a moment to run between steps.</summary>
    public async Task AdvanceAsync(TimeSpan total, TimeSpan step)
    {
        for (var moved = TimeSpan.Zero; moved < total; moved += step)
        {
            Time.Advance(step);
            await Task.Delay(5);
        }
    }

    /// <summary>Moves the fake time in <paramref name="step"/>s until <paramref name="call"/> completes, and returns its result.</summary>
    public async Task<T> RunAsync<T>(Task<T> call, TimeSpan step)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (!call.IsCompleted)
        {
            if (TimeProvider.System.GetElapsedTime(started) > RealTimeGuard)
            {
                throw new TimeoutException("The call did not complete while the fake time moved.");
            }

            Time.Advance(step);
            await Task.Delay(5);
        }

        return await call;
    }

    /// <summary>Waits (in real time) until <paramref name="handler"/> has seen <paramref name="calls"/> requests.</summary>
    public static async Task WaitForCallsAsync(ScriptedHttpMessageHandler handler, int calls)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (handler.Calls < calls)
        {
            if (TimeProvider.System.GetElapsedTime(started) > RealTimeGuard)
            {
                throw new TimeoutException($"The handler saw {handler.Calls} requests, not {calls}.");
            }

            await Task.Delay(5);
        }
    }

    public void Dispose() => Services.Dispose();
}
