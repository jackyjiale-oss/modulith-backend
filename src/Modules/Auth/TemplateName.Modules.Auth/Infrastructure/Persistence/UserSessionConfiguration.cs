using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    internal const int AuthMethodsMaxLength = 64;

    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("UserSessions");
        builder.HasKey(session => session.Id);
        builder.Property(session => session.Id).ValueGeneratedNever();

        builder.Property(session => session.AuthMethods).HasMaxLength(AuthMethodsMaxLength);
        builder.Property(session => session.DeviceName).HasMaxLength(UserSession.MaxDeviceNameLength);
        builder.Property(session => session.UserAgent).HasMaxLength(UserSession.MaxUserAgentLength);
        builder.Property(session => session.IpAddress).HasMaxLength(UserSession.MaxIpAddressLength);
        builder.Property(session => session.SecurityStamp).HasMaxLength(UserConfiguration.SecurityStampMaxLength);

        // The session refers to its user by id only; users are soft-deleted, never removed.
        builder.HasOne<User>().WithMany().HasForeignKey(session => session.UserId).OnDelete(DeleteBehavior.Restrict);

        // The whole chain belongs to the session (Ruling R7: the repository always loads every token).
        builder.HasMany(session => session.RefreshTokens).WithOne().HasForeignKey(token => token.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(session => session.RefreshTokens).HasField("_refreshTokens").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(session => session.DomainEvents);
    }
}
