namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>
/// Reads a user's effective permission codes: the same cached set (<c>perm:{userId}</c>, ADR 0016) that <c>IPermissionChecker</c> checks
/// against, so what a user is shown and what they may do never disagree.
/// </summary>
internal interface IPermissionReader
{
    /// <summary>The codes sorted ordinally; empty for an unknown, suspended or soft-deleted user.</summary>
    Task<IReadOnlyCollection<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken);
}
