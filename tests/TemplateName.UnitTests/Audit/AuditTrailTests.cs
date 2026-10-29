using TemplateName.Modules.Audit.Domain.Trails;
using TemplateName.SharedKernel.Auditing;

namespace TemplateName.UnitTests.Audit;

public sealed class AuditTrailTests
{
    private const string Ellipsis = "…";

    [Fact]
    public void Values_within_their_column_limits_are_kept_whole()
    {
        var module = new string('m', AuditTrail.MaxModuleLength);
        var entityType = new string('t', AuditTrail.MaxEntityTypeLength);
        var entityId = new string('k', AuditTrail.MaxEntityIdLength);

        var trail = Create(module, entityType, entityId);

        trail.Module.ShouldBe(module);
        trail.EntityType.ShouldBe(entityType);
        trail.EntityId.ShouldBe(entityId);
    }

    [Fact]
    public void An_entity_id_over_its_limit_is_cut_to_the_limit_and_ends_in_an_ellipsis()
    {
        var trail = Create("sample", "LeaveRequest", new string('k', AuditTrail.MaxEntityIdLength + 1));

        trail.EntityId.ShouldBe(new string('k', AuditTrail.MaxEntityIdLength - 1) + Ellipsis);
        trail.EntityId.Length.ShouldBe(AuditTrail.MaxEntityIdLength);
    }

    [Fact]
    public void An_entity_type_over_its_limit_is_cut_to_the_limit_and_ends_in_an_ellipsis()
    {
        var trail = Create("sample", new string('t', AuditTrail.MaxEntityTypeLength + 50), "1");

        trail.EntityType.ShouldBe(new string('t', AuditTrail.MaxEntityTypeLength - 1) + Ellipsis);
        trail.EntityType.Length.ShouldBe(AuditTrail.MaxEntityTypeLength);
    }

    [Fact]
    public void A_module_over_its_limit_is_cut_to_the_limit_and_ends_in_an_ellipsis()
    {
        var trail = Create(new string('m', AuditTrail.MaxModuleLength + 1_000), "LeaveRequest", "1");

        trail.Module.ShouldBe(new string('m', AuditTrail.MaxModuleLength - 1) + Ellipsis);
        trail.Module.Length.ShouldBe(AuditTrail.MaxModuleLength);
    }

    [Fact]
    public void A_cut_never_leaves_half_of_a_surrogate_pair()
    {
        // The emoji is two UTF-16 characters straddling the cut, so the pair is dropped whole: one character shorter than the limit.
        var entityId = new string('k', AuditTrail.MaxEntityIdLength - 2) + "😀" + "tail";

        var trail = Create("sample", "LeaveRequest", entityId);

        trail.EntityId.ShouldBe(new string('k', AuditTrail.MaxEntityIdLength - 2) + Ellipsis);
        trail.EntityId.Length.ShouldBe(AuditTrail.MaxEntityIdLength - 1);
        trail.EntityId.ShouldNotContain(character => char.IsSurrogate(character));
    }

    [Fact]
    public void The_cut_does_not_touch_the_other_fields()
    {
        var change = new EntityChange("LeaveRequest", new string('k', AuditTrail.MaxEntityIdLength + 1), EntityChangeAction.Updated, """{"Status":{"old":"Pending","new":"Approved"}}""");

        var trail = AuditTrail.Create(Guid.NewGuid(), DateTimeOffset.UnixEpoch, null, null, "sample", change, "trace");

        trail.Action.ShouldBe(EntityChangeAction.Updated);
        trail.Changes.ShouldBe(change.Changes);
        trail.TraceId.ShouldBe("trace");
    }

    [Fact]
    public void A_trace_id_at_its_limit_is_kept_whole_and_a_missing_one_stays_missing()
    {
        var traceId = new string('a', AuditTrail.MaxTraceIdLength);

        Create("sample", "LeaveRequest", "1", traceId).TraceId.ShouldBe(traceId);
        Create("sample", "LeaveRequest", "1", traceId: null).TraceId.ShouldBeNull();
    }

    [Fact]
    public void A_trace_id_over_its_limit_is_cut_to_the_limit_and_ends_in_an_ellipsis()
    {
        var trail = Create("sample", "LeaveRequest", "1", new string('a', AuditTrail.MaxTraceIdLength + 1));

        trail.TraceId.ShouldBe(new string('a', AuditTrail.MaxTraceIdLength - 1) + Ellipsis);
        trail.TraceId!.Length.ShouldBe(AuditTrail.MaxTraceIdLength);
    }

    [Fact]
    public void A_trace_id_cut_never_leaves_half_of_a_surrogate_pair()
    {
        var traceId = new string('a', AuditTrail.MaxTraceIdLength - 2) + "😀" + "tail";

        var trail = Create("sample", "LeaveRequest", "1", traceId);

        trail.TraceId.ShouldBe(new string('a', AuditTrail.MaxTraceIdLength - 2) + Ellipsis);
        trail.TraceId!.ShouldNotContain(character => char.IsSurrogate(character));
    }

    private static AuditTrail Create(string module, string entityType, string entityId, string? traceId = null)
        => AuditTrail.Create(
            Guid.NewGuid(),
            DateTimeOffset.UnixEpoch,
            userId: null,
            sessionId: null,
            module,
            new EntityChange(entityType, entityId, EntityChangeAction.Created, "{}"),
            traceId);
}
