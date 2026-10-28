using System.Reflection;
using System.Text.Json;
using TemplateName.SharedKernel.Auditing;

namespace TemplateName.UnitTests.SharedKernel.Auditing;

public sealed class EntityChangesRecordedEventTests
{
    [Fact]
    public void Domain_event_round_trips_through_System_Text_Json()
    {
        var original = new EntityChangesRecordedDomainEvent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            "Sample",
            [
                new EntityChange("LeaveRequest", "7f1c0d2e-0000-0000-0000-000000000001", EntityChangeAction.Created, """{"Reason":{"new":"Holiday"}}"""),
                new EntityChange("LeaveRequest", "7f1c0d2e-0000-0000-0000-000000000002", EntityChangeAction.SoftDeleted, """{"IsDeleted":{"old":false,"new":true}}"""),
            ]);

        var json = JsonSerializer.Serialize(original, original.GetType());
        var restored = (EntityChangesRecordedDomainEvent)JsonSerializer.Deserialize(json, typeof(EntityChangesRecordedDomainEvent))!;

        restored.Module.ShouldBe(original.Module);
        restored.UserId.ShouldBe(original.UserId);
        restored.SessionId.ShouldBe(original.SessionId);
        restored.TraceId.ShouldBe(original.TraceId);
        restored.Changes.ShouldBe(original.Changes);
    }

    [Theory]
    [InlineData(typeof(AuditedAttribute), AttributeTargets.Class, false)]
    [InlineData(typeof(AuditIgnoreAttribute), AttributeTargets.Property, true)]
    [InlineData(typeof(AuditMaskAttribute), AttributeTargets.Property, true)]
    public void Attributes_have_the_declared_targets(Type attributeType, AttributeTargets expectedTargets, bool expectedInherited)
    {
        var usage = attributeType.GetCustomAttribute<AttributeUsageAttribute>();

        usage.ShouldNotBeNull();
        usage.ValidOn.ShouldBe(expectedTargets);
        usage.Inherited.ShouldBe(expectedInherited);
    }
}
