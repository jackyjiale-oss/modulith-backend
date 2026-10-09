using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Audit;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class AuthAuditWriter(AuthDbContext context, IClientContext clientContext) : IAuthAuditWriter
{
    public void Record(AuthAuditLog entry)
    {
        entry.SetClientInfo(clientContext.IpAddress, clientContext.UserAgent, clientContext.TraceId);
        context.Set<AuthAuditLog>().Add(entry);
    }
}
