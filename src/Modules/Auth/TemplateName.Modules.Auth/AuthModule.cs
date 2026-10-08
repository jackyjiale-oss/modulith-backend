using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Localization;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Resources;

namespace TemplateName.Modules.Auth;

/// <summary>The Auth module's entry point: the host registers its services and maps its endpoints.</summary>
public static class AuthModule
{
    /// <summary>
    /// Registers the module's handlers and validators and its error messages (<c>AuthErrorMessages</c>). Call it after
    /// <c>AddInfrastructureCommon</c> and before <c>AddApplicationDecorators</c>.
    /// </summary>
    public static IServiceCollection AddAuthModule(this IServiceCollection services, IConfiguration configuration)
    {
        var assembly = typeof(AuthModule).Assembly;

        services.AddApplicationHandlers(assembly);
        services.AddErrorMessages<AuthErrorMessages>();

        return services;
    }

    /// <summary>Maps the module's endpoints; <paramref name="app"/> is the host's <c>/api/v1</c> group. Empty until the first endpoint lands.</summary>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        return app;
    }
}
