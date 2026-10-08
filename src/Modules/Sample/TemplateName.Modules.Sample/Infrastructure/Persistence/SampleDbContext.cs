using Microsoft.EntityFrameworkCore;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Sample.Application.Abstractions;

namespace TemplateName.Modules.Sample.Infrastructure.Persistence;

/// <summary>The Sample module's write model and outbox, in schema <c>sample</c>.</summary>
internal sealed class SampleDbContext(DbContextOptions<SampleDbContext> options) : DbContext(options), IUnitOfWork
{
    internal const string Schema = "sample";

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        => configurationBuilder.ApplyDefaultConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SampleDbContext).Assembly);
        modelBuilder.ApplyOutbox();
        modelBuilder.ApplySoftDeleteQueryFilters();
    }
}
