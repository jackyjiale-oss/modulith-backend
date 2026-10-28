using TemplateName.SharedKernel.Auditing;

namespace TemplateName.UnitTests.SharedKernel.Auditing;

public sealed class EntityChangeActionTests
{
    /// <summary>The numbers an outbox row holds; a pending row is read back with them, so they never change and are never reused.</summary>
    public static TheoryData<EntityChangeAction, int> PersistedValues => new()
    {
        { EntityChangeAction.Created, 0 },
        { EntityChangeAction.Updated, 1 },
        { EntityChangeAction.Deleted, 2 },
        { EntityChangeAction.SoftDeleted, 3 },
        { EntityChangeAction.Restored, 4 },
    };

    [Theory]
    [MemberData(nameof(PersistedValues))]
    public void Each_action_keeps_its_persisted_number(EntityChangeAction action, int expected)
        => ((int)action).ShouldBe(expected);

    [Fact]
    public void There_are_exactly_five_actions()
        => Enum.GetValues<EntityChangeAction>().Length.ShouldBe(5);
}
