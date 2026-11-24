using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Localization;
using TemplateName.Application.Common.Messaging;
using TemplateName.Infrastructure.Common.Http;
using TemplateName.Infrastructure.Common.Inbox;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Notifications.Application;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Devices;
using TemplateName.Modules.Notifications.Application.Sms;
using TemplateName.Modules.Notifications.Application.Scheduling;
using TemplateName.Modules.Notifications.Infrastructure.Catalog;
using TemplateName.Modules.Notifications.Infrastructure.Channels.Email;
using TemplateName.Modules.Notifications.Infrastructure.Channels.InApp;
using TemplateName.Modules.Notifications.Infrastructure.Channels.Push;
using TemplateName.Modules.Notifications.Infrastructure.Deliveries;
using TemplateName.Modules.Notifications.Infrastructure.Hubs;
using TemplateName.Modules.Notifications.Infrastructure.Observability;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;
using TemplateName.Modules.Notifications.Infrastructure.Retention;
using TemplateName.Modules.Notifications.Endpoints;
using TemplateName.Modules.Notifications.Infrastructure.Templates;
using TemplateName.Modules.Notifications.Resources;
using TemplateName.Web.Common.Security;
using HubOptions = TemplateName.Modules.Notifications.Application.HubTickets.HubOptions;

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
    /// options <c>Notifications:Delivery</c>), the email channel (see <see cref="AddEmailChannel"/>), the in-app channel, and the
    /// notification hub with its tickets and the in-app pusher (see <see cref="AddHub"/>), the retention of its time-bound rows and the
    /// recurring jobs that enforce it (see <see cref="AddRetention"/>), and the module's permission source
    /// (<see cref="NotificationsPermissionSource"/>), which the Auth seeder syncs like every <c>IPermissionSource</c>. Call it after <c>AddAuthModule</c> (whose
    /// <c>IUserContactDirectory</c> the scheduler uses) and before <c>AddApplicationDecorators</c>.
    /// </summary>
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddModuleDbContext<NotificationsDbContext>(NotificationsDbContext.Schema);
        services.AddInbox<NotificationsDbContext>();
        services.AddApplicationHandlers(typeof(NotificationsModule).Assembly);
        services.AddErrorMessages<NotificationsErrorMessages>();

        // The Auth seeder collects every registered source; this module never references Auth.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPermissionSource, NotificationsPermissionSource>());

        services.AddScoped<IInbox, ModuleInbox>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IInAppNotificationRepository, InAppNotificationRepository>();
        services.AddScoped<IPreferenceRepository, PreferenceRepository>();
        services.AddScoped<IHubTicketRepository, HubTicketRepository>();
        services.AddScoped<IPushDeviceRepository, PushDeviceRepository>();
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
        AddEmailChannel(services, configuration);
        AddHub(services, configuration);
        AddPush(services, configuration);
        AddSms(services, configuration);
        AddRetention(services, configuration);

        return services;
    }

    /// <summary>
    /// Registers the push channel: <see cref="PushOptions"/> (<c>Notifications:Push</c>) and <see cref="FcmOptions"/>
    /// (<c>Notifications:Push:Fcm</c>, whose project id and credentials are checked while push is enabled), both validated on start; the
    /// rate-limit policy of the device registration route (<see cref="PushDeviceRateLimit"/>; the device routes work whether or not push
    /// is enabled); and, only while <c>Notifications:Push:Enabled</c> is true, the <see cref="PushChannel"/>, the
    /// <see cref="FcmPushTransport"/> on the named client and resilience pipeline <c>push-provider</c>, and the Google token source. With
    /// push off no channel is registered, so a push delivery left pending is dead-lettered with <c>channel_unavailable</c>.
    /// </summary>
    internal static void AddPush(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PushOptions>()
            .Bind(configuration.GetSection(PushOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        var enabled = configuration.GetValue<bool>($"{PushOptions.SectionName}:{nameof(PushOptions.Enabled)}");
        services.AddOptions<FcmOptions>()
            .Bind(configuration.GetSection(FcmOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FcmOptions>>(new FcmOptionsValidation(enabled));
        services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<PushOptions>, IFixedWindowLimiterFactory>(
                (limiterOptions, pushOptions, limiterFactory) => PushDeviceRateLimit.AddPolicy(limiterOptions, pushOptions.Value, limiterFactory));

        if (!enabled)
        {
            return;
        }

        services.AddScoped<INotificationChannel, PushChannel>();
        services.AddSingleton<IPushTransport, FcmPushTransport>();
        services.AddSingleton<IFcmAccessTokenSource, GoogleFcmAccessTokenSource>();
        services.AddExternalHttpClient(FcmPushTransport.ClientName, FcmPushTransport.ClientName);
    }

    /// <summary>
    /// Registers the SMS channel's settings: <see cref="SmsOptions"/> (<c>Notifications:Sms</c>, validated on start). With SMS off no SMS
    /// delivery is created, so a type whose only channel is SMS is recorded with nothing to deliver.
    /// </summary>
    internal static void AddSms(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SmsOptions>()
            .Bind(configuration.GetSection(SmsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }

    /// <summary>
    /// Registers the notification hub (D12, ADR 0021): <see cref="HubOptions"/> (<c>Notifications:Hub</c>, validated on start), the
    /// <c>HubTicket</c> authentication scheme (<see cref="HubTicketAuthenticationHandler"/>; not a default scheme, so it runs only where
    /// a policy names it: the hub), SignalR with string enums in its JSON payloads, <see cref="SubClaimUserIdProvider"/> and
    /// <see cref="SignalRInAppNotificationPusher"/> as the in-app pusher (a singleton over the singleton hub context), and the rate-limit
    /// policy of the ticket endpoint (<see cref="HubTicketRateLimit"/>, whose windows come from the <see cref="IFixedWindowLimiterFactory"/>
    /// that <c>AddHttpSecurity</c> registers, so the host calls it too).
    /// </summary>
    internal static void AddHub(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<HubOptions>()
            .Bind(configuration.GetSection(HubOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddAuthentication()
            .AddScheme<HubTicketAuthenticationOptions, HubTicketAuthenticationHandler>(HubTicketAuthenticationOptions.SchemeName, configureOptions: null);
        services.AddSignalR()
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddSingleton<IUserIdProvider, SubClaimUserIdProvider>();
        services.AddSingleton<IInAppNotificationPusher, SignalRInAppNotificationPusher>();
        services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<HubOptions>, IFixedWindowLimiterFactory>(
                (limiterOptions, hubOptions, limiterFactory) => HubTicketRateLimit.AddPolicy(limiterOptions, hubOptions.Value, limiterFactory));
    }

    /// <summary>
    /// Registers <c>Notifications:Retention</c> (validated on start: a negative value is refused) and the four recurring cleanup jobs that
    /// delete what it covers: <c>NotificationsHubTicketsCleanup</c> hourly, <c>NotificationsRowsCleanup</c> (notifications with their
    /// deliveries, unless one is still pending), <c>NotificationsInAppCleanup</c> and <c>NotificationsPushDevicesCleanup</c> (deactivated
    /// push devices) daily. Requires <c>AddInfrastructureCommon</c>, which registers the scheduler and the batch settings.
    /// </summary>
    internal static void AddRetention(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<NotificationsRetentionOptions>()
            .Bind(configuration.GetSection(NotificationsRetentionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRecurringJob<HubTicketsCleanupJob>();
        services.AddRecurringJob<NotificationsCleanupJob>();
        services.AddRecurringJob<InAppNotificationsCleanupJob>();
        services.AddRecurringJob<PushDevicesCleanupJob>();
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

    /// <summary>
    /// Maps the module's endpoints; <paramref name="app"/> is the host's <c>/api/v1</c> group: the signed-in user's routes
    /// <c>notifications/...</c> (the inbox, the hub ticket, preferences and quiet hours) and the delivery administration in
    /// <c>admin/notifications/deliveries</c> (<c>GET</c> list and <c>POST .../{id}/retry</c>, each behind its permission).
    /// </summary>
    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapNotificationEndpoints();
        app.MapAdminDeliveryEndpoints();

        return app;
    }

    /// <summary>
    /// Maps the notification hub at <c>/hubs/notifications</c>, at the root, outside <c>/api/v1</c> (D12, ADR 0021): the WebSockets
    /// transport only (long polling and server-sent events would need a credential that works for many requests), the connection
    /// closed when its authentication expires, no stateful reconnect, and authorization by either the JWT bearer scheme
    /// (<c>Authorization</c> header only; it never reads a query string) or the <c>HubTicket</c> scheme. Map it with the other endpoints,
    /// after <c>UseAuthentication</c>, <c>UseCors</c>, <c>UseRateLimiter</c> and <c>UseAuthorization</c>, which all apply to it.
    /// </summary>
    public static IEndpointRouteBuilder MapNotificationsHub(this IEndpointRouteBuilder app)
    {
        app.MapHub<NotificationsHub>(NotificationsHub.Path, options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.CloseOnAuthenticationExpiration = true;

                // Off by default; said here because stateful reconnect keeps a resend buffer per connection (CVE-2026-70159, ADR 0021).
                options.AllowStatefulReconnects = false;
            })
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, HubTicketAuthenticationOptions.SchemeName)
                .RequireAuthenticatedUser());

        return app;
    }
}
