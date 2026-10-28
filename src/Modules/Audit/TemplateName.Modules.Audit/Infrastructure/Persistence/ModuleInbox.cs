using TemplateName.Infrastructure.Common.Inbox;
using TemplateName.Modules.Audit.Application.Abstractions;

namespace TemplateName.Modules.Audit.Infrastructure.Persistence;

/// <summary>The module's <see cref="IInbox"/>: the building block's inbox over <see cref="AuditDbContext"/>.</summary>
internal sealed class ModuleInbox(Inbox<AuditDbContext> inbox) : IInbox
{
    public Task<bool> HasProcessedAsync(Guid messageId, string consumer, CancellationToken cancellationToken)
        => inbox.HasProcessedAsync(messageId, consumer, cancellationToken);

    public void Record(Guid messageId, string consumer) => inbox.Record(messageId, consumer);
}
