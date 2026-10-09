using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Admin.Users.Get;

/// <summary>
/// Reads one user through the repositories, whose soft-delete filter hides deleted users (an unknown and a deleted id both answer
/// <see cref="UserErrors.NotFound"/>) and deleted roles. Only the fields an administrator needs leave the handler.
/// </summary>
internal sealed class GetUserQueryHandler(IUserRepository users, IRoleRepository roles, TimeProvider timeProvider)
    : IQueryHandler<GetUserQuery, UserResponse>
{
    public async Task<Result<UserResponse>> HandleAsync(GetUserQuery query, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(query.UserId, cancellationToken);
        if (user is null)
        {
            return UserErrors.NotFound(query.UserId);
        }

        var userRoles = await roles.GetByIdsAsync([.. user.Roles.Select(assignment => assignment.RoleId)], cancellationToken);

        return new UserResponse(
            user.Id,
            user.Email,
            user.EmailConfirmed,
            user.DisplayName,
            user.Locale,
            user.TimeZone,
            user.Status,
            HasPassword: user.PasswordHash is not null,
            IsLockedOut: user.IsLockedOut(timeProvider.GetUtcNow()),
            user.LockoutEnd,
            user.LastLoginAt,
            user.PasswordChangedAt,
            user.CreatedAt,
            [.. userRoles.OrderBy(role => role.Name, StringComparer.Ordinal).Select(role => new UserRoleResponse(role.Id, role.Name))]);
    }
}
