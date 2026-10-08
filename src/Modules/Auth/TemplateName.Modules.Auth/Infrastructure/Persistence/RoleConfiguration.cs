using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Roles;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Roles");
        builder.HasKey(role => role.Id);
        builder.Property(role => role.Id).ValueGeneratedNever();

        builder.Property(role => role.Name).HasMaxLength(Role.MaxNameLength);
        builder.Property(role => role.NormalizedName).HasMaxLength(Role.MaxNameLength);
        builder.Property(role => role.Description).HasMaxLength(Role.MaxDescriptionLength);
        builder.Property(role => role.RowVersion).IsRowVersion();

        // A soft-deleted role frees its name. The index also serves the role list's name sort; its name is what the create and rename
        // handlers listen for when two requests take the same name at once.
        builder.HasIndex(role => role.NormalizedName).IsUnique().HasFilter("[IsDeleted] = 0").HasDatabaseName(UniqueIndexNames.RoleName);

        // The role list's createdAt sort (review P7); like the name index it leaves out deleted roles.
        builder.HasIndex(role => new { role.CreatedAt, role.Id }).HasFilter("[IsDeleted] = 0");

        builder.HasMany(role => role.Permissions).WithOne().HasForeignKey(grant => grant.RoleId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(role => role.Permissions).HasField("_permissions").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(role => role.DomainEvents);
    }
}
