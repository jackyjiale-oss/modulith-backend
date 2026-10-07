using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace TemplateName.Infrastructure.Common.Persistence;

public static class MigrationExtensions
{
    /// <summary>
    /// Applies pending migrations for every module context registered with <c>includeInMigrations</c>, in registration order.
    /// Development only; other environments run EF migration bundles (ADR 0006).
    /// </summary>
    public static async Task MigrateModuleDatabasesAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        foreach (var registration in scope.ServiceProvider.GetServices<ModuleDbContextRegistration>())
        {
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(registration.ContextType);
            await context.Database.MigrateAsync(cancellationToken);
        }
    }
}
