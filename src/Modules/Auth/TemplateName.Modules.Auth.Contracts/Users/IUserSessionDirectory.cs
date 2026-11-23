namespace TemplateName.Modules.Auth.Contracts.Users;

/// <summary>
/// Lets another module ask which of a user's sign-in sessions are still active, so it never reads the Auth tables. The Notifications
/// module binds each push device to the session that registered it and asks before it schedules and before it sends a push. The
/// answer is always current: a session that was revoked or expired a moment ago is not in it.
/// </summary>
public interface IUserSessionDirectory
{
    /// <summary>The sessions among <paramref name="sessionIds"/> that are still active.</summary>
    /// <param name="userId">The user the sessions must belong to; a session of anyone else is never returned.</param>
    /// <param name="sessionIds">The session ids to check (the <c>sid</c> claim of the access tokens that registered them).</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>
    /// The subset that belongs to <paramref name="userId"/>, is not revoked and has not reached its expiry; empty for an empty input,
    /// which runs no query.
    /// </returns>
    Task<IReadOnlySet<Guid>> GetActiveSessionIdsAsync(Guid userId, IReadOnlyCollection<Guid> sessionIds, CancellationToken cancellationToken);
}
