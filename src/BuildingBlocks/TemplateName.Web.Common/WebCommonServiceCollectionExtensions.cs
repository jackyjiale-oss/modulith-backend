using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Localization;
using TemplateName.Web.Common.Errors;
using TemplateName.Web.Common.Identity;
using TemplateName.Web.Common.Resources;

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
}
