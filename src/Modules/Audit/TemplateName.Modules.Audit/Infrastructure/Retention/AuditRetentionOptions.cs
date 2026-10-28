using System.ComponentModel.DataAnnotations;

namespace TemplateName.Modules.Audit.Infrastructure.Retention;

/// <summary>
/// How long the Audit module keeps its audit trails before a recurring cleanup job deletes them (section <c>Audit:Retention</c>, ADR 0028).
/// A trail goes only when it is strictly older than the retention; <see langword="null"/> or zero turns the cleanup off and a negative value
/// is refused when the host starts.
/// </summary>
internal sealed class AuditRetentionOptions : IValidatableObject
{
    internal const string SectionName = "Audit:Retention";

    /// <summary>
    /// How long an audit trail is kept after <c>OccurredAt</c>. Off by default: the template ships no archive to move the trails to, so
    /// deleting them would lose them. A deployment that must keep them longer than its window archives them from the database first.
    /// </summary>
    public TimeSpan? Trails { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Trails < TimeSpan.Zero)
        {
            yield return new ValidationResult($"{nameof(Trails)} may not be negative; use zero to turn the cleanup off.", [nameof(Trails)]);
        }
    }
}
