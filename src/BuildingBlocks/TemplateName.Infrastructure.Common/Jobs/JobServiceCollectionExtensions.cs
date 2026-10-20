using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace TemplateName.Infrastructure.Common.Jobs;

public static class JobServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TJob"/> with the recurring-job scheduler (resolved in a scope of its own for every run). Registering
    /// the same type again changes nothing; two types with the same <see cref="IRecurringJob.Name"/> make the runner throw an
    /// <see cref="InvalidOperationException"/> the first time it is used. Requires <c>AddInfrastructureCommon</c>.
    /// </summary>
    public static IServiceCollection AddRecurringJob<TJob>(this IServiceCollection services)
        where TJob : class, IRecurringJob
    {
        services.TryAddScoped<TJob>();

        var isRegistered = services.Any(descriptor =>
            descriptor.ServiceType == typeof(RecurringJobRegistration)
            && descriptor.ImplementationInstance is RecurringJobRegistration registration
            && registration.JobType == typeof(TJob));
        if (!isRegistered)
        {
            services.AddSingleton(new RecurringJobRegistration(typeof(TJob)));
        }

        return services;
    }

    /// <summary>
    /// Registers the scheduler: the <c>Jobs</c> options (validated on start), each job's <c>Jobs:{Name}</c> options (validated when first
    /// read), the metrics, the singleton <see cref="RecurringJobRunner"/> and the background service that ticks it while
    /// <c>Jobs:Enabled</c> is set.
    /// </summary>
    internal static IServiceCollection AddRecurringJobScheduler(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JobsOptions>().Bind(configuration.GetSection(JobsOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<RecurringJobOptions>>(new RecurringJobOptionsSetup(configuration)));

        // A null name validates every named instance, that is every job's options.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RecurringJobOptions>>(
            new DataAnnotationValidateOptions<RecurringJobOptions>(name: null)));

        services.AddMetrics();
        services.TryAddSingleton<RecurringJobMetrics>();
        services.TryAddSingleton(RecurringJobRunner.Create);
        services.AddHostedService<RecurringJobService>();

        return services;
    }
}
