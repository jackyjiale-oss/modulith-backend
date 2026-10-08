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
    /// Saves like <see cref="SaveChangesAsync"/>, but answers false instead of throwing when a unique index refuses a row (SQL Server
    /// errors 2601 and 2627). Nothing is saved then, and the pending changes are discarded. For a create whose uniqueness check can lose
    /// a race with a simultaneous create, such as an administrator creating an account (the unique email index decides).
    /// </summary>
    Task<bool> SaveChangesUnlessDuplicateAsync(CancellationToken cancellationToken);
}
