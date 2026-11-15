using Microsoft.EntityFrameworkCore;
using ProjectPrefix.Infrastructure.Common.Outbox;
using ProjectPrefix.Infrastructure.Common.Persistence;
using ProjectPrefix.Modules.ModuleName.Application.Abstractions;

namespace ProjectPrefix.Modules.ModuleName.Infrastructure.Persistence;

/// <summary>The ModuleName module's write model and outbox, in schema <c>module-lower</c>.</summary>
internal sealed class ModuleNameDbContext(DbContextOptions<ModuleNameDbContext> options) : DbContext(options), IUnitOfWork
{
    internal const string Schema = "module-lower";

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        => configurationBuilder.ApplyDefaultConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ModuleNameDbContext).Assembly);
        modelBuilder.ApplyOutbox();
    }
}
