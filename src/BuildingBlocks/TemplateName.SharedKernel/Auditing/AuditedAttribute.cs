namespace TemplateName.SharedKernel.Auditing;

/// <summary>
/// Marks an entity class whose changes are written to the audit trail. Only the marked class is audited: the attribute is not
/// inherited, so a derived entity opts in on its own.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class AuditedAttribute : Attribute;
