namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>Saves the changes made through this module's repositories in one transaction. Each module defines its own.</summary>
internal interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Saves like <see cref="SaveChangesAsync"/>, but answers false instead of throwing when the only failure is that the inbox already
    /// holds the record staged through <see cref="IInbox.Record"/> (another delivery of the same event saved first; ADR 0018). Nothing
    /// is saved then and the pending changes are discarded, so the caller treats the event as processed. Any other failure, including
    /// a unique violation on this module's own tables such as the notification index, is still thrown.
    /// </summary>
    Task<bool> SaveChangesUnlessInboxDuplicateAsync(CancellationToken cancellationToken);
}
