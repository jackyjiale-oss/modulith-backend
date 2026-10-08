using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Auth.Domain.Roles;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    internal const int NameMaxLength = 100;
    internal const int DescriptionMaxLength = 500;

    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Roles");
        builder.HasKey(role => role.Id);
        builder.Property(role => role.Id).ValueGeneratedNever();

        builder.Property(role => role.Name).HasMaxLength(NameMaxLength);
        builder.Property(role => role.NormalizedName).HasMaxLength(NameMaxLength);
        builder.Property(role => role.Description).HasMaxLength(DescriptionMaxLength);
        builder.Property(role => role.RowVersion).IsRowVersion();

        // A soft-deleted role frees its name.
        builder.HasIndex(role => role.NormalizedName).IsUnique().HasFilter("[IsDeleted] = 0");

        builder.HasMany(role => role.Permissions).WithOne().HasForeignKey(grant => grant.RoleId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(role => role.Permissions).HasField("_permissions").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(role => role.DomainEvents);
    }
}
