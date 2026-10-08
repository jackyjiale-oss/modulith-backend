using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Localization;
using TemplateName.Application.Common.Messaging;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Sample.Application;
using TemplateName.Modules.Sample.Application.Abstractions;
using TemplateName.Modules.Sample.Endpoints;
using TemplateName.Modules.Sample.Infrastructure.Persistence;
using TemplateName.Modules.Sample.Resources;

namespace TemplateName.Modules.Sample;

/// <summary>The Sample module's entry point: the host registers its services and maps its endpoints.</summary>
public static class SampleModule
{
    /// <summary>
    /// Registers the module's context (schema <c>sample</c>), outbox, handlers and validators, repository, error messages
    /// (<c>SampleErrorMessages</c>) and permission source (<c>sample.leave_request.*</c>, synced into the permission table by the Auth
    /// module's seeder). Call it after <c>AddInfrastructureCommon</c> and before <c>AddApplicationDecorators</c>.
    /// </summary>
    public static IServiceCollection AddSampleModule(this IServiceCollection services)
    {
        var assembly = typeof(SampleModule).Assembly;

        services.AddModuleDbContext<SampleDbContext>(SampleDbContext.Schema);
        services.AddOutbox<SampleDbContext>(assembly);
        services.AddApplicationHandlers(assembly);
        services.AddErrorMessages<SampleErrorMessages>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPermissionSource, SamplePermissionSource>());

        services.AddScoped<ILeaveRequestRepository, LeaveRequestRepository>();
        services.AddScoped<IUnitOfWork>(serviceProvider => serviceProvider.GetRequiredService<SampleDbContext>());

        return services;
    }

    /// <summary>Maps the module's endpoints; <paramref name="app"/> is the host's <c>/api/v1</c> group.</summary>
    public static IEndpointRouteBuilder MapSampleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapLeaveRequestEndpoints();

        return app;
    }
}
