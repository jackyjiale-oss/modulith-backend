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
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["RateLimiting:GlobalPermitLimit"] = "100000",
                ["Outbox:Enabled"] = "false",
            }));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
}
