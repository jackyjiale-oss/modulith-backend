using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Admin.Users.Create;

/// <summary>
/// Creates an account without a password on behalf of <paramref name="ActorId"/>. <paramref name="RoleIds"/> null or empty gives the
/// default <c>User</c> role; otherwise exactly those roles (which needs <c>auth.user.assign_roles</c>). Answers the new user's id.
/// </summary>
internal sealed record CreateUserCommand(Guid ActorId, string Email, string DisplayName, string Locale, IReadOnlyCollection<Guid>? RoleIds)
    : ICommand<Guid>;
