namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>Saves the changes made through this module's repositories in one transaction. Each module defines its own.</summary>
internal interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Saves like <see cref="SaveChangesAsync"/>, but answers false instead of throwing when an optimistic-concurrency check fails
    /// (another request changed one of the rows first). Nothing is saved then, and the pending changes are discarded. For handlers whose
    /// answer must not depend on such a race, such as login, where a 409 would show that the account exists.
    /// </summary>
    Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Saves like <see cref="SaveChangesAsync"/>, but answers false instead of throwing when the unique index
    /// <paramref name="uniqueIndexName"/> (see <see cref="UniqueIndexNames"/>) refuses a row (SQL Server errors 2601 and 2627). Nothing
    /// is saved then, and the pending changes are discarded. For a create whose uniqueness check can lose a race with a simultaneous
    /// create (a registration, an administrator's account or role). A violation of any other unique index is still thrown, so it cannot
    /// be mistaken for the caller's meaning (for example "this address is taken").
    /// </summary>
    Task<bool> SaveChangesUnlessDuplicateAsync(string uniqueIndexName, CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="SaveChangesUnlessDuplicateAsync(string, CancellationToken)"/> for a save that more than one unique index can refuse
    /// (see <see cref="UniqueIndexNames"/>): answers false when the refusing index is any of <paramref name="uniqueIndexNames"/>. A
    /// violation of an index that is not in the list is still thrown. For a link or a first sign-in, which a simultaneous request can
    /// beat on the identity or on the user's one login per provider, both of which the caller answers alike.
    /// </summary>
    /// <exception cref="ArgumentException">The list is empty or holds a blank name.</exception>
    Task<bool> SaveChangesUnlessDuplicateAsync(IReadOnlyCollection<string> uniqueIndexNames, CancellationToken cancellationToken);
}
