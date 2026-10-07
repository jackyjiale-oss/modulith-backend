using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TemplateName.Application.Common.Data;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.Infrastructure.Common.Persistence;

namespace TemplateName.Infrastructure.Common;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers the system clock, the <c>ConnectionStrings</c> and <c>Outbox</c> options (validated on start) and the <c>Database</c>
    /// options, the read-side connection factory and Dapper's <c>DateOnly</c> type handler, the EF Core save interceptors and the
    /// concurrency-conflict exception handler. Call it before <c>AddWebCommon</c>, so the concurrency handler runs before the global
    /// one, and before any <c>AddModuleDbContext</c> or <c>AddOutbox</c>.
    /// </summary>
    public static IServiceCollection AddInfrastructureCommon(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<ConnectionStringsOptions>().Bind(configuration.GetSection(ConnectionStringsOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName));
        services.AddOptions<OutboxOptions>().Bind(configuration.GetSection(OutboxOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();

        services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();
        services.AddScoped<AuditableEntityInterceptor>();
        services.AddScoped<SoftDeleteInterceptor>();
        services.AddSingleton<DomainEventsToOutboxInterceptor>();
        services.AddExceptionHandler<ConcurrencyExceptionHandler>();

        // Dapper's type handlers are process-wide; adding the same handler again replaces it.
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());

        return services;
    }
}
