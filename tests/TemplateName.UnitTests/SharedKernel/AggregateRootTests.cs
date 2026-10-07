using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.SharedKernel;

public sealed class AggregateRootTests
{
    [Fact]
    public void Raise_adds_event()
    {
        var aggregate = new TestAggregate();

        aggregate.DoSomething();

        aggregate.DomainEvents.Count.ShouldBe(1);
        aggregate.DomainEvents[0].ShouldBeOfType<TestDomainEvent>();
    }

    [Fact]
    public void ClearDomainEvents_empties_list()
    {
        var aggregate = new TestAggregate();
        aggregate.DoSomething();

        aggregate.ClearDomainEvents();

        aggregate.DomainEvents.ShouldBeEmpty();
    }

    private sealed record TestDomainEvent : IDomainEvent;

    private sealed class TestAggregate : AggregateRoot<Guid>
    {
        public void DoSomething() => Raise(new TestDomainEvent());
    }
}
