using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using TemplateName.Application.Common.Localization;
using TemplateName.Application.Common.Messaging;
using TemplateName.Infrastructure.Common.Inbox;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Scheduling;
using TemplateName.Modules.Notifications.Infrastructure.Catalog;
using TemplateName.Modules.Notifications.Infrastructure.Channels.Email;
using TemplateName.Modules.Notifications.Infrastructure.Channels.InApp;
using TemplateName.Modules.Notifications.Infrastructure.Deliveries;
using TemplateName.Modules.Notifications.Infrastructure.Observability;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;
using TemplateName.Modules.Notifications.Infrastructure.Templates;
using TemplateName.Modules.Notifications.Resources;

namespace TemplateName.Modules.Notifications;

/// <summary>The Notifications module's entry point: the host registers its services and maps its endpoints and hub.</summary>
public static class NotificationsModule
{
    /// <summary>
    /// Registers the module's <c>NotificationsDbContext</c> (schema <c>notify</c>, migrated with the other module contexts), its inbox,
    /// repositories and unit of work, handlers and validators (including the consumers of Auth's integration events), the
    /// <see cref="NotificationScheduler"/> they share, the counters (meter <c>TemplateName.Notifications</c>, added to OpenTelemetry),
    /// error messages (<c>NotificationsErrorMessages</c>), the <see cref="NotificationCatalog"/> (a singleton over every
    /// <see cref="INotificationTypeSource"/>), the hosted service that validates it when the host starts, the template renderer with
    /// its options (<c>Notifications:Templates</c>), the delivery worker (<see cref="DeliveryDispatcher"/>, its hosted service and
    /// options <c>Notifications:Delivery</c>), the email channel (see <see cref="AddEmailChannel"/>), and the in-app channel with its
    /// pusher (<c>NullInAppNotificationPusher</c> until the hub exists). Call it after <c>AddAuthModule</c> (whose
    /// <c>IUserContactDirectory</c> the scheduler uses) and before <c>AddApplicationDecorators</c>.
    /// </summary>
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddModuleDbContext<NotificationsDbContext>(NotificationsDbContext.Schema);
        services.AddInbox<NotificationsDbContext>();
        services.AddApplicationHandlers(typeof(NotificationsModule).Assembly);
        services.AddErrorMessages<NotificationsErrorMessages>();

        services.AddScoped<IInbox, ModuleInbox>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IInAppNotificationRepository, InAppNotificationRepository>();
        services.AddScoped<IPreferenceRepository, PreferenceRepository>();
        services.AddScoped<IHubTicketRepository, HubTicketRepository>();
        services.AddScoped<IUnitOfWork>(serviceProvider => serviceProvider.GetRequiredService<NotificationsDbContext>());
        services.AddScoped<NotificationScheduler>();

        // The counters; the meter is added to OpenTelemetry, which exports it whenever the host exports metrics (OTLP configured).
        services.AddSingleton<INotificationsMetrics, NotificationsMetrics>();
        services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddMeter(NotificationsMetrics.MeterName));

        services.AddSingleton<INotificationTypeSource, AuthNotificationTypeSource>();
        services.AddSingleton<NotificationCatalog>();
        services.AddHostedService<NotificationCatalogStartupCheck>();

        services.AddOptions<TemplateOptions>()
            .Bind(configuration.GetSection(TemplateOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<EmbeddedTemplateStore>();
        services.AddSingleton<INotificationRenderer, ScribanNotificationRenderer>();

        // The delivery worker: a singleton dispatcher polled by a hosted service while Notifications:Delivery:Enabled is set. Channels
        // are resolved per delivery in the worker's scope, next to the after-save work they may queue.
        services.AddOptions<DeliveryOptions>()
            .Bind(configuration.GetSection(DeliveryOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<DeliveryDispatcher>();
        services.AddHostedService<DeliveryBackgroundService>();
        services.AddScoped<DeliverySaveCallbacks>();
        services.AddScoped<INotificationChannel, InAppChannel>();
        services.AddSingleton<IInAppNotificationPusher, NullInAppNotificationPusher>();
        AddEmailChannel(services, configuration);

        return services;
    }

    /// <summary>
    /// Registers the email channel (<c>EmailChannel</c>, scoped like every channel) over the SMTP transport (<c>SmtpEmailTransport</c>,
    /// a singleton behind <c>IEmailTransport</c>) and its options <c>Notifications:Email</c>, validated on start together with
    /// <c>LegacyEmailSettingsCheck</c>: a host that still has any <c>Auth:Email</c> key in <paramref name="configuration"/> does not
    /// start (D15).
    /// </summary>
    internal static void AddEmailChannel(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<EmailOptions>>(new LegacyEmailSettingsCheck(configuration));
        services.AddSingleton<IEmailTransport, SmtpEmailTransport>();
        services.AddScoped<INotificationChannel, EmailChannel>();
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
