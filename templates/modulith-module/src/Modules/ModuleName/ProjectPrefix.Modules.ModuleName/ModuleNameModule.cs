using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProjectPrefix.Application.Common.Identity;
using ProjectPrefix.Application.Common.Localization;
using ProjectPrefix.Application.Common.Messaging;
using ProjectPrefix.Infrastructure.Common.Outbox;
using ProjectPrefix.Infrastructure.Common.Persistence;
using ProjectPrefix.Modules.ModuleName.Application;
using ProjectPrefix.Modules.ModuleName.Application.Abstractions;
using ProjectPrefix.Modules.ModuleName.Infrastructure.Persistence;
using ProjectPrefix.Modules.ModuleName.Resources;

namespace ProjectPrefix.Modules.ModuleName;

/// <summary>The ModuleName module's entry point: the host registers its services and maps its endpoints.</summary>
public static class ModuleNameModule
{
    /// <summary>
    /// Registers the module's context (schema <c>module-lower</c>), outbox, handlers and validators, error messages
    /// (<c>ModuleNameErrorMessages</c>), permission source and unit of work. Call it after <c>AddInfrastructureCommon</c> and before
    /// <c>AddApplicationDecorators</c>.
    /// </summary>
    public static IServiceCollection AddModuleNameModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var assembly = typeof(ModuleNameModule).Assembly;

        services.AddModuleDbContext<ModuleNameDbContext>(ModuleNameDbContext.Schema);
        services.AddOutbox<ModuleNameDbContext>(assembly);
        services.AddApplicationHandlers(assembly);
        services.AddErrorMessages<ModuleNameErrorMessages>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPermissionSource, ModuleNamePermissionSource>());

        services.AddScoped<IUnitOfWork>(serviceProvider => serviceProvider.GetRequiredService<ModuleNameDbContext>());

        return services;
    }

    /// <summary>Maps the module's endpoints; <paramref name="app"/> is the host's <c>/api/v1</c> group. Nothing is mapped yet.</summary>
    public static IEndpointRouteBuilder MapModuleNameEndpoints(this IEndpointRouteBuilder app) => app;
}
