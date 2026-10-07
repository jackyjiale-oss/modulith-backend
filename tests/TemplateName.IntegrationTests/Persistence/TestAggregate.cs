using TemplateName.SharedKernel;

namespace TemplateName.IntegrationTests.Persistence;

/// <summary>An auditable, soft-deletable aggregate that exercises the persistence conventions and interceptors.</summary>
internal sealed class TestAggregate : AggregateRoot<Guid>, IAuditable, ISoftDeletable
{
    private TestAggregate()
    {
    }

    public string Name { get; private set; } = string.Empty;

    public DateTime CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTime? UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public bool IsDeleted { get; private set; }

    public DateTime? DeletedAt { get; private set; }

    public Guid? DeletedBy { get; private set; }

    public static TestAggregate Create(string name, DateTimeOffset now) => new() { Id = SequentialGuid.Create(now), Name = name };

    public void Rename(string name) => Name = name;
}
