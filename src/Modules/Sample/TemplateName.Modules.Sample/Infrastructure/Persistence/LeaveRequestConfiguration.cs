using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Sample.Domain.LeaveRequests;

namespace TemplateName.Modules.Sample.Infrastructure.Persistence;

internal sealed class LeaveRequestConfiguration : IEntityTypeConfiguration<LeaveRequest>
{
    internal const int ReasonMaxLength = 500;

    public void Configure(EntityTypeBuilder<LeaveRequest> builder)
    {
        builder.ToTable("LeaveRequests");
        builder.HasKey(leaveRequest => leaveRequest.Id);
        builder.Property(leaveRequest => leaveRequest.Id).ValueGeneratedNever();

        // DateOnly maps to date and the byte-backed status enum to tinyint by default.
        builder.Property(leaveRequest => leaveRequest.Reason).HasMaxLength(ReasonMaxLength);
        builder.Property(leaveRequest => leaveRequest.RowVersion).IsRowVersion();

        builder.HasIndex(leaveRequest => leaveRequest.EmployeeId);

        builder.Ignore(leaveRequest => leaveRequest.DomainEvents);
    }
}
