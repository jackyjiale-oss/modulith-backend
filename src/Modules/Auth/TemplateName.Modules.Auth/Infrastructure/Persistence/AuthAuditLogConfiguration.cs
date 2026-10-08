using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Auth.Domain.Audit;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class AuthAuditLogConfiguration : IEntityTypeConfiguration<AuthAuditLog>
{
    internal const int EventTypeMaxLength = 64;
    internal const int FailureReasonMaxLength = 128;

    public void Configure(EntityTypeBuilder<AuthAuditLog> builder)
    {
        builder.ToTable("AuthAuditLogs");
        builder.HasKey(entry => entry.Id);

        // bigint identity: the log is append-only and ordered by insertion.
        builder.Property(entry => entry.Id).UseIdentityColumn();

        builder.Property(entry => entry.EventType).HasMaxLength(EventTypeMaxLength);
        builder.Property(entry => entry.FailureReason).HasMaxLength(FailureReasonMaxLength);
        builder.Property(entry => entry.AttemptedIdentifier).HasMaxLength(AuthAuditLog.MaxAttemptedIdentifierLength);
        builder.Property(entry => entry.IpAddress).HasMaxLength(AuthAuditLog.MaxIpAddressLength);
        builder.Property(entry => entry.UserAgent).HasMaxLength(AuthAuditLog.MaxUserAgentLength);
        builder.Property(entry => entry.TraceId).HasMaxLength(AuthAuditLog.MaxTraceIdLength);

        // No foreign key to Users: the log outlives what it describes and also records attempts for unknown users.
        // One index per audit filter, newest first.
        builder.HasIndex(entry => new { entry.UserId, entry.OccurredAt }).IsDescending(false, true);
        builder.HasIndex(entry => new { entry.EventType, entry.OccurredAt }).IsDescending(false, true);
        builder.HasIndex(entry => new { entry.IpAddress, entry.OccurredAt }).IsDescending(false, true);
    }
}
