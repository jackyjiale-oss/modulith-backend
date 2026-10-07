using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>Hosts the real API in the <c>Testing</c> environment. One instance is shared by the whole assembly.</summary>
public sealed class IntegrationTestWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // A host setting, not an in-memory source, so tests can lower the limit with UseSetting on a derived factory.
        builder.UseSetting("RateLimiting:GlobalPermitLimit", "100000");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Outbox:Enabled"] = "false",

                // Keep test output to problems: no per-request or host-lifetime lines.
                ["Serilog:MinimumLevel:Default"] = "Warning",
            }));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
}
