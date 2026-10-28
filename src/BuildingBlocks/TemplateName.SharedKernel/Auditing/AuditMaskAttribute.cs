namespace TemplateName.SharedKernel.Auditing;

/// <summary>
/// Marks a property of an <see cref="AuditedAttribute">audited</see> entity whose change is recorded but whose values are not: the
/// audit trail holds <c>"****"</c> in their place.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class AuditMaskAttribute : Attribute;
