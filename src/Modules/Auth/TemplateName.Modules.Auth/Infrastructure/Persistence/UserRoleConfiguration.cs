using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        builder.ToTable("UserRoles");
        builder.HasKey(assignment => new { assignment.UserId, assignment.RoleId });

        // The user refers to the role by id only; the foreign key keeps that reference valid (roles are soft-deleted, never removed).
        builder.HasOne<Role>().WithMany().HasForeignKey(assignment => assignment.RoleId).OnDelete(DeleteBehavior.Restrict);
    }
}
