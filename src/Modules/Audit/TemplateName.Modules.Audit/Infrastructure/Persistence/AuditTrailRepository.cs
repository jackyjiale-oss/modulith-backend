using TemplateName.Modules.Audit.Application.Abstractions;
using TemplateName.Modules.Audit.Domain.Trails;

namespace TemplateName.Modules.Audit.Infrastructure.Persistence;

internal sealed class AuditTrailRepository(AuditDbContext context) : IAuditTrailRepository
{
    public void Add(AuditTrail trail) => context.Set<AuditTrail>().Add(trail);
}
