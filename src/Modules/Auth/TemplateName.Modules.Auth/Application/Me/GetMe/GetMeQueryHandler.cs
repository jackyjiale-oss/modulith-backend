using Dapper;
using TemplateName.Application.Common.Data;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Me.GetMe;

/// <summary>
/// Reads the caller through Dapper and the permission set through <see cref="IPermissionReader"/> (the cached set the permission checks
/// use). Fails closed: a caller whose user is unknown, soft-deleted or suspended gets <see cref="UserErrors.NotFound"/>, which the endpoint
/// answers with the same 401 as a request without a token, although the access token itself is still valid (decision D9).
/// </summary>
internal sealed class GetMeQueryHandler(
    IDbConnectionFactory connectionFactory,
    ICurrentUser currentUser,
    IPermissionReader permissionReader) : IQueryHandler<GetMeQuery, MeResponse>
{
    // Dapper bypasses the EF Core soft-delete filters, so the queries repeat them for users and roles (ADR 0006). Assignments have no
    // flag of their own: they are hidden through their role.
    private const string Sql = """
        SELECT Id, Email, EmailConfirmed, DisplayName, Locale, TimeZone
        FROM auth.Users
        WHERE Id = @UserId AND IsDeleted = 0 AND Status = @ActiveStatus;

        SELECT r.Name
        FROM auth.UserRoles AS ur
        INNER JOIN auth.Roles AS r ON r.Id = ur.RoleId
        WHERE ur.UserId = @UserId AND r.IsDeleted = 0;
        """;

    public async Task<Result<MeResponse>> HandleAsync(GetMeQuery query, CancellationToken cancellationToken)
    {
        // The endpoint requires an authenticated caller; without a user id there is nobody to describe.
        if (currentUser.UserId is not { } userId)
        {
            return Result.Failure<MeResponse>(UserErrors.NotFound(Guid.Empty));
        }

        UserRow? user;
        string[] roles;
        await using (var connection = await connectionFactory.OpenConnectionAsync(cancellationToken))
        {
            await using var results = await connection.QueryMultipleAsync(new CommandDefinition(
                Sql,
                new { UserId = userId, ActiveStatus = (int)UserStatus.Active },
                cancellationToken: cancellationToken));
            user = await results.ReadSingleOrDefaultAsync<UserRow>();
            roles = [.. await results.ReadAsync<string>()];
        }

        if (user is null)
        {
            return Result.Failure<MeResponse>(UserErrors.NotFound(userId));
        }

        Array.Sort(roles, StringComparer.Ordinal);
        var permissions = await permissionReader.GetPermissionsAsync(userId, cancellationToken);

        return new MeResponse(
            user.Id,
            user.Email,
            user.EmailConfirmed,
            user.DisplayName,
            user.Locale,
            user.TimeZone,
            roles.AsReadOnly(),
            permissions);
    }

    /// <summary>The user columns, bound by Dapper through the constructor in column order.</summary>
    private sealed record UserRow(Guid Id, string Email, bool EmailConfirmed, string DisplayName, string Locale, string TimeZone);
}
