using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TemplateName.Application.Common.Localization;

public static class LocalizationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <c>*ErrorMessages</c> resource set whose marker class is <typeparamref name="TResource"/> (the <c>.resx</c> files sit
    /// next to it with the same base name) and the <see cref="IErrorMessageLocalizer"/> that searches every registered set. Sets are
    /// searched in registration order; registering the same set twice has no effect.
    /// </summary>
    public static IServiceCollection AddErrorMessages<TResource>(this IServiceCollection services)
    {
        services.AddLocalization();

        var isRegistered = services.Any(descriptor =>
            descriptor.ServiceType == typeof(ErrorMessageSource)
            && descriptor.ImplementationInstance is ErrorMessageSource { ResourceType: var type }
            && type == typeof(TResource));

        if (!isRegistered)
        {
            services.AddSingleton(new ErrorMessageSource(typeof(TResource)));
        }

        services.TryAddSingleton<IErrorMessageLocalizer, ErrorMessageLocalizer>();
        return services;
    }
}
