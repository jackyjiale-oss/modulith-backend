namespace TemplateName.SharedKernel;

/// <summary>An entity that is flagged as deleted instead of removed. A save interceptor sets these values through EF property entries.</summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; }

    DateTime? DeletedAt { get; }

    Guid? DeletedBy { get; }
}
