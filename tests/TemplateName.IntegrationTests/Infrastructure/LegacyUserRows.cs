using Microsoft.EntityFrameworkCore;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// Writes a user into a database migrated to an older Auth schema, for the upgrade tests. The current model has columns that schema does
/// not (the phone number, from <c>AddPhoneNumbers</c>), so EF cannot insert the aggregate there; this inserts only the columns of
/// <c>InitialAuth</c>, which every later schema keeps.
/// </summary>
internal static class LegacyUserRows
{
    public static Task InsertAsync(DbContext context, User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(user);

        return context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO [auth].[Users] ([Id], [Email], [NormalizedEmail], [EmailConfirmed], [PasswordHash], [SecurityStamp], [Status],
                [DisplayName], [Locale], [TimeZone], [AccessFailedCount], [CreatedAt], [IsDeleted])
            VALUES ({user.Id}, {user.Email}, {user.NormalizedEmail}, {user.EmailConfirmed}, {user.PasswordHash}, {user.SecurityStamp},
                {(int)user.Status}, {user.DisplayName}, {user.Locale}, {user.TimeZone}, 0, {user.CreatedAt}, 0)
            """,
            cancellationToken);
    }
}
