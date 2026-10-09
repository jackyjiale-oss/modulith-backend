using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TemplateName.Application.Common.Data;
using TemplateName.Application.Common.Localization;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Security;
using TemplateName.Infrastructure.Common.Idempotency;
using TemplateName.Infrastructure.Common.Messaging;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Infrastructure.Common.Resources;
using TemplateName.Infrastructure.Common.Security;

namespace TemplateName.Infrastructure.Common;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers the system clock, the <c>ConnectionStrings</c>, <c>Outbox</c> and <c>Idempotency</c> options (validated on start) and
    /// the <c>Database</c> options, the read-side connection factory and Dapper's <c>DateOnly</c> type handler, the EF Core save
    /// interceptors, the concurrency-conflict exception handler, the <c>platform</c> context (idempotency keys), the messages of
    /// their error codes (<c>InfrastructureErrorMessages</c>), the in-process <see cref="IIntegrationEventPublisher"/> (singleton) and
    /// the <see cref="IOutboxMessageContext"/> the outbox dispatcher sets (scoped), and the <see cref="ISecretProtector"/> (singleton,
    /// Data Protection purpose <c>TemplateName.Secrets.v1</c>) with a Data Protection registration that has no key store of its own. Call it before
    /// <c>AddWebCommon</c>, so the concurrency handler runs before the global one, and before any <c>AddModuleDbContext</c> or
    /// <c>AddOutbox</c>, so the <c>platform</c> migrations apply first.
    /// </summary>
    public static IServiceCollection AddInfrastructureCommon(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<ConnectionStringsOptions>().Bind(configuration.GetSection(ConnectionStringsOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName));
        services.AddOptions<OutboxOptions>().Bind(configuration.GetSection(OutboxOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<IdempotencyOptions>().Bind(configuration.GetSection(IdempotencyOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();

        services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();
        services.AddScoped<AuditableEntityInterceptor>();
        services.AddScoped<SoftDeleteInterceptor>();
        services.AddSingleton<DomainEventsToOutboxInterceptor>();
        services.AddScoped<OutboxMessageContext>();
        services.AddScoped<IOutboxMessageContext>(provider => provider.GetRequiredService<OutboxMessageContext>());
        services.AddSingleton<IIntegrationEventPublisher, InProcessIntegrationEventPublisher>();

        // Data Protection without a key store: the module that owns the key ring adds the application name and the persistence
        // (Auth: auth.DataProtectionKeys). Without one the keys are kept on the local file system.
        services.AddDataProtection();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddExceptionHandler<ConcurrencyExceptionHandler>();
        services.AddErrorMessages<InfrastructureErrorMessages>();
        services.AddModuleDbContext<PlatformDbContext>(PlatformDbContext.Schema);

        // Dapper's type handlers are process-wide; adding the same handler again replaces it.
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());

        return services;
    }
}
