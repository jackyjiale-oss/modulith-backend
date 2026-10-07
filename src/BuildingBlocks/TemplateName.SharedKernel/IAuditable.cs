namespace TemplateName.SharedKernel;

/// <summary>An entity whose creation and last update are stamped by a save interceptor. All timestamps are UTC.</summary>
public interface IAuditable
{
    DateTime CreatedAt { get; }

    Guid? CreatedBy { get; }

    DateTime? UpdatedAt { get; }

    Guid? UpdatedBy { get; }
}
