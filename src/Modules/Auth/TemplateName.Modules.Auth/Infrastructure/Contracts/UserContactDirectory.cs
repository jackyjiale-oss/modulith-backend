using Dapper;
using TemplateName.Application.Common.Data;
using TemplateName.Modules.Auth.Contracts.Users;

namespace TemplateName.Modules.Auth.Infrastructure.Contracts;

/// <summary>
/// The Auth side of <see cref="IUserContactDirectory"/>: one Dapper query on <c>auth.Users</c> that reads only the address, name,
/// language and time zone (never a hash, a stamp or a status). Dapper bypasses the EF Core soft-delete filter, so the SQL repeats it
/// (ADR 0006). A suspended user is returned, because security notices must still reach them. <c>Locale</c> and <c>TimeZone</c> are not
/// nullable columns and are passed on as stored (a locale can be empty); the caller applies its own fallbacks.
/// </summary>
internal sealed class UserContactDirectory(IDbConnectionFactory connectionFactory) : IUserContactDirectory
{
    private const string Sql = """
        SELECT Id AS UserId, Email, DisplayName, Locale, TimeZone
        FROM auth.Users
        WHERE Id = @UserId AND IsDeleted = 0;
        """;

    public async Task<UserContact?> FindAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<UserContact>(
            new CommandDefinition(Sql, new { UserId = userId }, cancellationToken: cancellationToken));
    }
}
