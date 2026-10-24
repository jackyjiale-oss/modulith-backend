using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace TemplateName.Infrastructure.Common.Http;

/// <summary>
/// Fills the named <see cref="ResilienceOptions"/> of a pipeline: the values of that pipeline when it is a built-in one (any other
/// name starts from the values of <see cref="ResiliencePipelines.Default"/>), then its section <c>Resilience:Pipelines:{name}</c>.
/// </summary>
internal sealed class ResilienceOptionsSetup(IConfiguration configuration) : IConfigureNamedOptions<ResilienceOptions>
{
    public void Configure(string? name, ResilienceOptions options)
    {
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        switch (name)
        {
            case ResiliencePipelines.Critical:
                // Five jittered retries wait 23 s to 46 s in all, so the total must be well above that.
                options.Retries = 5;
                options.TotalTimeout = TimeSpan.FromSeconds(90);
                break;

            case ResiliencePipelines.Hibp:
                // Inside the 2 s of the checker: a 1 s attempt, one retry after a short delay (the default 2 s would use up the total).
                options.AttemptTimeout = TimeSpan.FromSeconds(1);
                options.TotalTimeout = TimeSpan.FromSeconds(2);
                options.Retries = 1;
                options.BaseDelay = TimeSpan.FromMilliseconds(200);
                break;
        }

        configuration.GetSection(ResilienceOptions.SectionName).GetSection(name).Bind(options);
    }

    public void Configure(ResilienceOptions options) => Configure(Options.DefaultName, options);
}
