using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class UserExternalLoginConfiguration : IEntityTypeConfiguration<UserExternalLogin>
{
    public void Configure(EntityTypeBuilder<UserExternalLogin> builder)
    {
        builder.ToTable("UserExternalLogins");
        builder.HasKey(login => login.Id);

        // The key is known before the save, as the audit trail of an added entity requires (AuditingTests).
        builder.Property(login => login.Id).ValueGeneratedNever();

        builder.Property(login => login.Provider).HasMaxLength(UserExternalLogin.ProviderMaxLength);
        builder.Property(login => login.Subject).HasMaxLength(UserExternalLogin.SubjectMaxLength);
        builder.Property(login => login.Email).HasMaxLength(UserExternalLogin.EmailMaxLength);

        // The foreign key to Users and its cascade are declared on the owner (UserConfiguration).

        // An identity at a provider belongs to one user, and a user holds one login for each provider. The names are the ones handlers
        // turn a refusal of into their own answer (UniqueIndexNames, also EF's default names, so the schema does not change). The second
        // index also serves the lookup of a user's logins by the foreign key.
        builder.HasIndex(login => new { login.Provider, login.Subject }).IsUnique().HasDatabaseName(UniqueIndexNames.ExternalLoginIdentity);
        builder.HasIndex(login => new { login.UserId, login.Provider }).IsUnique().HasDatabaseName(UniqueIndexNames.ExternalLoginProviderPerUser);
    }
}
