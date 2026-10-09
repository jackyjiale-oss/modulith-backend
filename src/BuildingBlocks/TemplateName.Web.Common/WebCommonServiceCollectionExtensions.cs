using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Localization;
using TemplateName.Web.Common.Errors;
using TemplateName.Web.Common.Identity;
using TemplateName.Web.Common.Resources;
using TemplateName.Web.Common.Security;

namespace TemplateName.Web.Common;

public static class WebCommonServiceCollectionExtensions
{
    /// <summary>
    /// Registers ProblemDetails (with <c>traceId</c>, <c>code</c> and a localized <c>detail</c>), the shared error messages
    /// (<c>CommonErrorMessages</c>), the global exception handler and the current-user accessor.
    /// </summary>
    public static IServiceCollection AddWebCommon(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
        services.AddErrorMessages<CommonErrorMessages>();
        services.AddProblemDetails(options => options.CustomizeProblemDetails = ProblemDetailsSetup.Customize);
        services.AddExceptionHandler<GlobalExceptionHandler>();

        // Surface malformed request bodies as exceptions so the global handler renders them as 400 ProblemDetails.
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

        return services;
    }

    /// <summary>
    /// Registers authorization with <see cref="PermissionAuthorizationHandler"/> (scoped) for the requirements <c>RequirePermission</c>
    /// adds, and a fallback policy that requires an authenticated user: an endpoint with no authorization metadata of its own is
    /// protected, and an anonymous endpoint has to say <c>.AllowAnonymous()</c>. Each <c>RequirePermission</c> builds its own policy;
    /// there is no dynamic policy provider (decision D6). Call it together with explicit <c>UseAuthentication</c> and
    /// <c>UseAuthorization</c> after <c>UseRouting</c> (decision D7 places authentication before request localization, so the saved
    /// locale wins): once authorization services exist and the pipeline does not call <c>UseAuthorization</c>, <c>WebApplication</c>
    /// adds it ahead of <c>UseRouting</c> by itself, where no endpoint is known and the fallback policy answers 401 to every request.
    /// Requires <see cref="AddWebCommon"/> (<see cref="ICurrentUser"/>) and a registered <see cref="IPermissionChecker"/>.
    /// </summary>
    public static IServiceCollection AddPermissionAuthorization(this IServiceCollection services)
    {
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return services;
    }
}
