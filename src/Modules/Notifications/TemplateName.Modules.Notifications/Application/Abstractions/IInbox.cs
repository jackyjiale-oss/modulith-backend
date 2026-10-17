namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>
/// The module's view of its inbox (<c>notify.InboxMessages</c>, ADR 0018): which integration events each consumer has processed. A
/// consumer checks <see cref="HasProcessedAsync"/>, stages its rows, calls <see cref="Record"/> and saves once through
/// <see cref="IUnitOfWork.SaveChangesUnlessInboxDuplicateAsync"/>, so the inbox row commits or rolls back with its own rows.
/// </summary>
internal interface IInbox
{
    /// <summary>Whether <paramref name="consumer"/> has a saved record of <paramref name="messageId"/>.</summary>
    Task<bool> HasProcessedAsync(Guid messageId, string consumer, CancellationToken cancellationToken);

    /// <summary>Stages the record that <paramref name="consumer"/> has processed <paramref name="messageId"/>; saved with the caller's own changes.</summary>
    void Record(Guid messageId, string consumer);
}
