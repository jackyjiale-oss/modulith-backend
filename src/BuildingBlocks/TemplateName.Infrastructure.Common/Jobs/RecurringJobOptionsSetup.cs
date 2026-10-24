using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace TemplateName.Infrastructure.Common.Jobs;

/// <summary>Binds the named <see cref="RecurringJobOptions"/> of a job from its section <c>Jobs:{Name}</c>.</summary>
internal sealed class RecurringJobOptionsSetup(IConfiguration configuration) : IConfigureNamedOptions<RecurringJobOptions>
{
    public void Configure(string? name, RecurringJobOptions options)
    {
        if (!string.IsNullOrEmpty(name))
        {
            configuration.GetSection(JobsOptions.SectionName).GetSection(name).Bind(options);
        }
    }

    public void Configure(RecurringJobOptions options) => Configure(Options.DefaultName, options);
}
