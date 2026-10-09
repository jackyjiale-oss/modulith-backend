using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Localization;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Infrastructure.Catalog;
using TemplateName.Modules.Notifications.Resources;

namespace TemplateName.Modules.Notifications;

/// <summary>The Notifications module's entry point: the host registers its services and maps its endpoints and hub.</summary>
public static class NotificationsModule
{
    /// <summary>
    /// Registers the module's handlers and validators, error messages (<c>NotificationsErrorMessages</c>), the
    /// <see cref="NotificationCatalog"/> (a singleton over every <see cref="INotificationTypeSource"/>) and the hosted service that
    /// validates it when the host starts. Call it after <c>AddAuthModule</c> and before <c>AddApplicationDecorators</c>.
    /// </summary>
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddApplicationHandlers(typeof(NotificationsModule).Assembly);
        services.AddErrorMessages<NotificationsErrorMessages>();

        services.AddSingleton<NotificationCatalog>();
        services.AddHostedService<NotificationCatalogStartupCheck>();

        return services;
    }

    /// <summary>Maps the module's endpoints; <paramref name="app"/> is the host's <c>/api/v1</c> group. None yet.</summary>
    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        return app;
    }

    /// <summary>Maps the module's SignalR hub at the root, outside <c>/api/v1</c>. None yet.</summary>
    public static IEndpointRouteBuilder MapNotificationsHub(this IEndpointRouteBuilder app)
    {
        return app;
    }
}
