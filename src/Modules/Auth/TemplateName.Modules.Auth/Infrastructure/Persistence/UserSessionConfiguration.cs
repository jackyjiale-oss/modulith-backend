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

        // One index per sort of the session list, which always filters on the owner (review P7); the leading UserId also serves the
        // foreign key and the revoke-all lookups.
        builder.HasIndex(session => new { session.UserId, session.LastSeenAt, session.Id });
        builder.HasIndex(session => new { session.UserId, session.CreatedAt, session.Id });

        // The cleanup job deletes the sessions past their expiry, and the revoked ones once the retention has passed.
        builder.HasIndex(session => session.ExpiresAt);
        builder.HasIndex(session => session.RevokedAt).HasFilter("[RevokedAt] IS NOT NULL");

        // When the user last proved their identity in the session (sign-in or step-up), the auth_time of its access tokens. Not null:
        // migration AddMultiFactorAuthentication filled it from CreatedAt for the sessions that existed before it.
        builder.Property(session => session.AuthenticatedAt);

        builder.Ignore(session => session.DomainEvents);
    }
}
