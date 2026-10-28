using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Infrastructure.Common.Inbox;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Audit.Application;
using TemplateName.Modules.Audit.Application.Abstractions;
using TemplateName.Modules.Audit.Endpoints;
using TemplateName.Modules.Audit.Infrastructure.Persistence;
using TemplateName.Modules.Audit.Infrastructure.Retention;

namespace TemplateName.Modules.Audit;

/// <summary>The Audit module's entry point: the host registers its services and maps its endpoints.</summary>
public static class AuditModule
{
    /// <summary>
    /// Registers the module's <c>AuditDbContext</c> (schema <c>audit</c>, migrated with the other module contexts, with its <c>ready</c>
    /// health check), its inbox (and the inbox cleanup job), the optional trail retention (<c>Audit:Retention</c> and the <c>AuditTrailsCleanup</c> job), the handlers (the consumer of <c>EntityChangesRecordedIntegrationEvent</c>
    /// that writes the trail), the repository and unit of work, and the module's permission source (<see cref="AuditPermissionSource"/>),
    /// which the Auth seeder syncs like every <c>IPermissionSource</c>; this module never references another. The consumer is resolved by
    /// event type, so the order among the module registrations does not matter. Call it after <c>AddInfrastructureCommon</c> and before
    /// <c>AddApplicationDecorators</c>.
    /// </summary>
    public static IServiceCollection AddAuditModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddModuleDbContext<AuditDbContext>(AuditDbContext.Schema);
        services.AddInbox<AuditDbContext>();
        services.AddApplicationHandlers(typeof(AuditModule).Assembly);

        // The Auth seeder collects every registered source; this module never references Auth.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPermissionSource, AuditPermissionSource>());

        services.AddScoped<IInbox, ModuleInbox>();
        services.AddScoped<IAuditTrailRepository, AuditTrailRepository>();
        services.AddScoped<IUnitOfWork>(serviceProvider => serviceProvider.GetRequiredService<AuditDbContext>());

        AddRetention(services, configuration);

        return services;
    }

    /// <summary>
    /// Registers <c>Audit:Retention</c> (validated on start: a negative value is refused) and the recurring job
    /// <c>AuditTrailsCleanup</c> (daily), which deletes the trails older than <c>Audit:Retention:Trails</c> and does nothing until that is
    /// set (ADR 0028). Requires <c>AddInfrastructureCommon</c>, which registers the scheduler and the batch settings.
    /// </summary>
    internal static void AddRetention(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuditRetentionOptions>()
            .Bind(configuration.GetSection(AuditRetentionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRecurringJob<AuditTrailsCleanupJob>();
    }

    /// <summary>Maps the module's endpoints; <paramref name="app"/> is the host's <c>/api/v1</c> group: the audit trail list (<c>admin/audit/trails</c>).</summary>
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app) => app.MapAdminAuditTrailEndpoints();
}
