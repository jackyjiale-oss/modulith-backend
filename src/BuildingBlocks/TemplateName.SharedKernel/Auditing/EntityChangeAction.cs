namespace TemplateName.SharedKernel.Auditing;

/// <summary>What happened to an audited entity in one save.</summary>
public enum EntityChangeAction
{
    /// <summary>The entity was added.</summary>
    Created,

    /// <summary>The entity was modified.</summary>
    Updated,

    /// <summary>The entity row was removed.</summary>
    Deleted,

    /// <summary>The entity was marked deleted and kept (<see cref="ISoftDeletable"/>).</summary>
    SoftDeleted,

    /// <summary>A soft-deleted entity was made visible again.</summary>
    Restored,
}
