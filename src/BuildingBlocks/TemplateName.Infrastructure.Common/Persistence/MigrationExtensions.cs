using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace TemplateName.Infrastructure.Common.Persistence;

public static class MigrationExtensions
{
    /// <summary>
    /// Applies pending migrations for every module context registered with <c>includeInMigrations</c>, in registration order. The host
    /// calls it on start when <c>Database:ApplyMigrationsOnStartup</c> is set (Development only), the test harness calls it once, and the
    /// API's <c>migrate</c> mode calls it in production (ADR 0006).
    /// </summary>
    /// <exception cref="OptionsValidationException"><c>ConnectionStrings:Database</c> is missing.</exception>
    public static async Task MigrateModuleDatabasesAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        // Validate first, so a missing connection string fails with the options error rather than a SQL client argument error.
        _ = services.GetRequiredService<IOptions<ConnectionStringsOptions>>().Value;

        await using var scope = services.CreateAsyncScope();
        foreach (var registration in scope.ServiceProvider.GetServices<ModuleDbContextRegistration>())
        {
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(registration.ContextType);
            await context.Database.MigrateAsync(cancellationToken);
        }
    }
}
