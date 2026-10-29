namespace TemplateName.SharedKernel.Auditing;

/// <summary>Marks a property of an <see cref="AuditedAttribute">audited</see> entity that is never written to the audit trail.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class AuditIgnoreAttribute : Attribute;
