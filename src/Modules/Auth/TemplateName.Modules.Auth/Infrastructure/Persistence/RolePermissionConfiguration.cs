using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Auth.Domain.Roles;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("RolePermissions");
        builder.HasKey(grant => new { grant.RoleId, grant.PermissionId });

        // Permissions are deprecated, never deleted, so a grant always points at an existing row.
        builder.HasOne<Permission>().WithMany().HasForeignKey(grant => grant.PermissionId).OnDelete(DeleteBehavior.Restrict);
    }
}
