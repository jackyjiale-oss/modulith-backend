using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Auth.Domain.Roles;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    internal const int CodeMaxLength = 128;
    internal const int ModuleMaxLength = 64;
    internal const int NameMaxLength = 200;
    internal const int DescriptionMaxLength = 500;

    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("Permissions");
        builder.HasKey(permission => permission.Id);
        builder.Property(permission => permission.Id).ValueGeneratedNever();

        builder.Property(permission => permission.Code).HasMaxLength(CodeMaxLength);
        builder.Property(permission => permission.Module).HasMaxLength(ModuleMaxLength);
        builder.Property(permission => permission.Name).HasMaxLength(NameMaxLength);
        builder.Property(permission => permission.Description).HasMaxLength(DescriptionMaxLength);

        builder.HasIndex(permission => permission.Code).IsUnique();
    }
}
