namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>
/// The cached permission sets of users (ADR 0016). Every change to a role's permissions or a user's role assignments removes the
/// affected users' entries, so the change applies to their next request on this instance (other instances see it within 30 seconds).
/// </summary>
internal interface IPermissionCache
{
    /// <summary>Removes the cached permission set of each user, so the next check reloads it from the database.</summary>
    Task InvalidateUsersAsync(IEnumerable<Guid> userIds, CancellationToken cancellationToken);
}
