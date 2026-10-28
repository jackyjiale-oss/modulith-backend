using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Infrastructure.Common.Auditing;
using TemplateName.Infrastructure.Common.Outbox;

namespace TemplateName.Infrastructure.Common.Persistence;

public static class ModuleDbContextExtensions
{
    /// <summary>
    /// Registers a module context on SQL Server with its migrations history table in the module's <paramref name="schema"/>, retry on
    /// transient failures, the audit stamping, soft-delete, audit trail and domain-events-to-outbox interceptors, and a <c>ready</c> health
    /// check named after the schema. The connection string is read when a context is created, so test overrides apply. Requires
    /// <c>AddInfrastructureCommon</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="schema">The module's schema (lower-case module name).</param>
    /// <param name="connectionStringName">The entry under <c>ConnectionStrings</c> to connect with.</param>
    /// <param name="includeInMigrations">Whether <see cref="MigrationExtensions.MigrateModuleDatabasesAsync"/> migrates this context.</param>
    public static IServiceCollection AddModuleDbContext<TContext>(
        this IServiceCollection services,
        string schema,
        string connectionStringName = "Database",
        bool includeInMigrations = true)
        where TContext : DbContext
    {
        services.AddDbContext<TContext>((serviceProvider, options) =>
        {
            var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString(connectionStringName);
            options.UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", schema).EnableRetryOnFailure());

            // Audit stamping runs before the soft-delete conversion, so a soft delete sets DeletedAt/By but not UpdatedAt/By. The
            // audit trail runs after both, so a soft delete is already an update of IsDeleted and the stamps are already set (and
            // skipped by rule). It runs before the domain events, so both kinds of outbox row are added to the same save as plain
            // tracked entities (ADR 0007).
            options.AddInterceptors(
                serviceProvider.GetRequiredService<AuditableEntityInterceptor>(),
                serviceProvider.GetRequiredService<SoftDeleteInterceptor>(),
                serviceProvider.GetRequiredService<AuditTrailInterceptor>(),
                serviceProvider.GetRequiredService<DomainEventsToOutboxInterceptor>());
        });

        services.AddHealthChecks().AddDbContextCheck<TContext>(name: schema, tags: ["ready"]);

        if (includeInMigrations)
        {
            services.AddSingleton(new ModuleDbContextRegistration(typeof(TContext)));
        }

        return services;
    }
}
