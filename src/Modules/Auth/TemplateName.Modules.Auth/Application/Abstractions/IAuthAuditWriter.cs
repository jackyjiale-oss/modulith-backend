using TemplateName.Modules.Auth.Domain.Audit;

namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>Stages audit log entries in the module's unit of work, so they are saved with the handler's changes.</summary>
internal interface IAuthAuditWriter
{
    /// <summary>Fills the client address, user agent and trace id from <see cref="IClientContext"/> and stages the entry.</summary>
    void Record(AuthAuditLog entry);
}
