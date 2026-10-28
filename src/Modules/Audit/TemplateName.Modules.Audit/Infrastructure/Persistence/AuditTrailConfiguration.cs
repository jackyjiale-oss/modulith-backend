using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Audit.Domain.Trails;

namespace TemplateName.Modules.Audit.Infrastructure.Persistence;

internal sealed class AuditTrailConfiguration : IEntityTypeConfiguration<AuditTrail>
{
    public void Configure(EntityTypeBuilder<AuditTrail> builder)
    {
        // The JSON check lets an operator query the column with JSON_VALUE and refuses text that is not a JSON document.
        builder.ToTable("AuditTrails", table => table.HasCheckConstraint("CK_AuditTrails_Changes_IsJson", "ISJSON([Changes]) = 1"));
        builder.HasKey(trail => trail.Id);

        // bigint identity: the trail is append-only, and Id is the unique tie-breaker of its sort.
        builder.Property(trail => trail.Id).UseIdentityColumn();

        builder.Property(trail => trail.Module).HasMaxLength(AuditTrail.MaxModuleLength);
        builder.Property(trail => trail.EntityType).HasMaxLength(AuditTrail.MaxEntityTypeLength);
        builder.Property(trail => trail.EntityId).HasMaxLength(AuditTrail.MaxEntityIdLength);
        builder.Property(trail => trail.Action).HasConversion<string>().HasMaxLength(AuditTrail.MaxActionLength).IsUnicode(false);
        builder.Property(trail => trail.TraceId).HasMaxLength(AuditTrail.MaxTraceIdLength);

        // No foreign keys: the rows describe other modules' entities and outlive them. Ascending indexes ending in (OccurredAt, Id) like
        // the sort (ADR 0010, review P7); SQL Server reads them backwards for the other direction. "History of this record", "what did
        // this person change", "everything in this save", and the unfiltered list with the retention delete.
        builder.HasIndex(trail => new { trail.EntityType, trail.EntityId, trail.OccurredAt, trail.Id });
        builder.HasIndex(trail => new { trail.UserId, trail.OccurredAt, trail.Id });
        builder.HasIndex(trail => trail.ChangeSetId);
        builder.HasIndex(trail => new { trail.OccurredAt, trail.Id });
    }
}
