using TemplateName.Infrastructure.Common.Inbox;
using TemplateName.Modules.Notifications.Application.Abstractions;

namespace TemplateName.Modules.Notifications.Infrastructure.Persistence;

/// <summary>The module's <see cref="IInbox"/>: the building block's inbox over <see cref="NotificationsDbContext"/>.</summary>
internal sealed class ModuleInbox(Inbox<NotificationsDbContext> inbox) : IInbox
{
    public Task<bool> HasProcessedAsync(Guid messageId, string consumer, CancellationToken cancellationToken)
        => inbox.HasProcessedAsync(messageId, consumer, cancellationToken);

    public void Record(Guid messageId, string consumer) => inbox.Record(messageId, consumer);
}
