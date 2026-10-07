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

        // One index per list sort, ending in (sort column, Id) to match the keyset ORDER BY (review P7, LeaveRequestSortFields);
        // the employee filter leads its index, which also serves lookups by EmployeeId alone.
        builder.HasIndex(leaveRequest => new { leaveRequest.EmployeeId, leaveRequest.CreatedAt, leaveRequest.Id });
        builder.HasIndex(leaveRequest => new { leaveRequest.CreatedAt, leaveRequest.Id });
        builder.HasIndex(leaveRequest => new { leaveRequest.StartDate, leaveRequest.Id });

        builder.Ignore(leaveRequest => leaveRequest.DomainEvents);
    }
}
