namespace TemplateName.Modules.Auth.Contracts.Users;

/// <summary>
/// Lets another module look up the contact details of a user the Auth module owns, so it never reads the Auth tables. The call is
/// synchronous inside the process and always current; the caller takes a snapshot of what it needs.
/// </summary>
public interface IUserContactDirectory
{
    /// <summary>Finds a user's contact details.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>
    /// The contact, or null when no such user exists or the user was deleted. A suspended user is returned: security notices still
    /// reach them.
    /// </returns>
    Task<UserContact?> FindAsync(Guid userId, CancellationToken cancellationToken);
}
