namespace TemplateName.SharedKernel.Auditing;

/// <summary>
/// What happened to an audited entity in one save. An outbox row stores it as its number, so the values are explicit and never change
/// or get reused.
/// </summary>
public enum EntityChangeAction
{
    /// <summary>The entity was added.</summary>
    Created = 0,

    /// <summary>The entity was modified.</summary>
    Updated = 1,

    /// <summary>The entity row was removed.</summary>
    Deleted = 2,

    /// <summary>The entity was marked deleted and kept (<see cref="ISoftDeletable"/>).</summary>
    SoftDeleted = 3,

    /// <summary>A soft-deleted entity was made visible again.</summary>
    Restored = 4,
}
