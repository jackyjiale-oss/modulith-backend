using Dapper;
using TemplateName.Application.Common.Data;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.ExternalLogins.List;

/// <summary>
/// Reads the caller's external logins and whether the caller has a password through Dapper. Dapper bypasses the EF Core model, so the
/// statements keep to the caller's rows and repeat the soft-delete filter themselves (<c>IsDeleted = 0</c>, ADR 0006): the first only
/// answers for an active, undeleted user, and the second reads the logins of that undeleted user. Neither selects a subject or the
/// password hash. Fails closed: a caller whose user is unknown, soft-deleted or suspended gets <see cref="UserErrors.NotFound"/>, which the
/// endpoint answers with the same 401 as a request without a token, although the access token itself is still valid (decision D9). A
/// switched-off provider's login is listed like any other, by the key it was linked under. The list is not paged: a user has at most one
/// login for each provider.
/// </summary>
internal sealed class ListExternalLoginsQueryHandler(IDbConnectionFactory connectionFactory, ICurrentUser currentUser)
    : IQueryHandler<ListExternalLoginsQuery, ExternalLoginsResponse>
{
    private const string Sql = """
        SELECT CAST(CASE WHEN PasswordHash IS NULL THEN 0 ELSE 1 END AS bit) AS HasPassword
        FROM auth.Users
        WHERE Id = @UserId AND IsDeleted = 0 AND Status = @ActiveStatus;

        SELECT l.Id, l.Provider, l.Email, l.LinkedAt, l.LastUsedAt
        FROM auth.UserExternalLogins AS l
        INNER JOIN auth.Users AS u ON u.Id = l.UserId
        WHERE l.UserId = @UserId AND u.IsDeleted = 0
        ORDER BY l.Provider, l.Id;
        """;

    public async Task<Result<ExternalLoginsResponse>> HandleAsync(ListExternalLoginsQuery query, CancellationToken cancellationToken)
    {
        // The endpoint requires an authenticated caller; without a user id there is nobody to describe.
        if (currentUser.UserId is not { } userId)
        {
            return Result.Failure<ExternalLoginsResponse>(UserErrors.NotFound(Guid.Empty));
        }

        bool? hasPassword;
        LoginRow[] rows;
        await using (var connection = await connectionFactory.OpenConnectionAsync(cancellationToken))
        {
            await using var results = await connection.QueryMultipleAsync(new CommandDefinition(
                Sql,
                new { UserId = userId, ActiveStatus = (int)UserStatus.Active },
                cancellationToken: cancellationToken));
            hasPassword = await results.ReadSingleOrDefaultAsync<bool?>();
            rows = [.. await results.ReadAsync<LoginRow>()];
        }

        if (hasPassword is not { } passwordSet)
        {
            return Result.Failure<ExternalLoginsResponse>(UserErrors.NotFound(userId));
        }

        // datetime2 reads back as DateTimeKind.Unspecified; the columns hold UTC.
        var logins = rows
            .Select(row => new ExternalLoginResponse(row.Id, row.Provider, row.Email, AsUtc(row.LinkedAt), row.LastUsedAt is { } used ? AsUtc(used) : null))
            .ToArray();

        return new ExternalLoginsResponse(passwordSet, logins);
    }

    private static DateTimeOffset AsUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>The login columns, bound by Dapper through the constructor in column order.</summary>
    private sealed record LoginRow(Guid Id, string Provider, string? Email, DateTime LinkedAt, DateTime? LastUsedAt);
}
