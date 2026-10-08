using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using TemplateName.SharedKernel;

namespace TemplateName.Infrastructure.Common.Persistence;

/// <summary>Model conventions every module <see cref="DbContext"/> applies.</summary>
public static class ModelConventions
{
    /// <summary>The name of the query filter that hides soft-deleted rows; pass it to <c>IgnoreQueryFilters([...])</c> to include them.</summary>
    public const string SoftDeleteFilterName = "SoftDelete";

    /// <summary>
    /// Maps every <see cref="DateTime"/> and nullable <see cref="DateTime"/> to <c>datetime2(3)</c>, stored as UTC and read back with
    /// <see cref="DateTimeKind.Utc"/>. Every <see cref="DateTimeOffset"/> and nullable <see cref="DateTimeOffset"/> gets the same
    /// column, holding its UTC instant, and is read back with offset zero. Call it from <c>ConfigureConventions</c>.
    /// </summary>
    public static void ApplyDefaultConventions(this ModelConfigurationBuilder builder)
    {
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>().HavePrecision(3);
        builder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>().HavePrecision(3);
        builder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>().HavePrecision(3);
        builder.Properties<DateTimeOffset?>().HaveConversion<UtcDateTimeOffsetConverter>().HavePrecision(3);
    }

    /// <summary>
    /// Adds the named query filter <see cref="SoftDeleteFilterName"/> (<c>!IsDeleted</c>) to every <see cref="ISoftDeletable"/> entity.
    /// Call it at the end of <c>OnModelCreating</c>, after the entities are configured. Dapper queries bypass it (ADR 0006).
    /// </summary>
    public static void ApplySoftDeleteQueryFilters(this ModelBuilder modelBuilder)
    {
        // Query filters may only be declared on the root of an inheritance hierarchy; derived types inherit them.
        var softDeletableTypes = modelBuilder.Model.GetEntityTypes()
            .Where(entityType => entityType.BaseType is null && !entityType.IsOwned() && typeof(ISoftDeletable).IsAssignableFrom(entityType.ClrType));

        foreach (var entityType in softDeletableTypes)
        {
            var entity = Expression.Parameter(entityType.ClrType, "entity");
            var isDeleted = Expression.Call(
                typeof(EF), nameof(EF.Property), [typeof(bool)], entity, Expression.Constant(nameof(ISoftDeletable.IsDeleted)));
            var filter = Expression.Lambda(Expression.Not(isDeleted), entity);

            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(SoftDeleteFilterName, filter);
        }
    }
}
