using TemplateName.Modules.Audit.Domain.Trails;

namespace TemplateName.Modules.Audit.Application.Abstractions;

internal interface IAuditTrailRepository
{
    /// <summary>Stages a new trail row; it is written when the unit of work saves.</summary>
    void Add(AuditTrail trail);
}
