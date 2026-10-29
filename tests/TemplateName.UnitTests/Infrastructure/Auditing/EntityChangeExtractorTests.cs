using System.Text.Json.Nodes;
using TemplateName.Infrastructure.Common.Auditing;
using TemplateName.SharedKernel.Auditing;

namespace TemplateName.UnitTests.Infrastructure.Auditing;

public sealed class EntityChangeExtractorTests
{
    private static readonly Guid ItemId = Guid.Parse("0a7c1b52-3d4e-4f60-8a91-b2c3d4e5f607");

    [Fact]
    public void Added_entity_is_Created_with_non_null_audited_values_only()
    {
        using var context = new AuditTestDbContext();
        context.Items.Add(NewItem());
        context.ChangeTracker.DetectChanges();

        var change = Extract(context).ShouldHaveSingleItem();

        change.EntityType.ShouldBe(nameof(AuditedItem));
        change.EntityId.ShouldBe(ItemId.ToString());
        change.Action.ShouldBe(EntityChangeAction.Created);
        AssertChanges(change, """
            {
              "Id": { "new": "0a7c1b52-3d4e-4f60-8a91-b2c3d4e5f607" },
              "Title": { "new": "First" },
              "Count": { "new": 3 },
              "Due": { "new": "2026-10-28" },
              "State": { "new": "Draft" },
              "Flag": { "new": "None" }
            }
            """);
    }

    [Fact]
    public void Modified_entity_is_Updated_with_old_and_new_of_changed_audited_properties_only()
    {
        using var context = new AuditTestDbContext();
        var item = NewItem();
        item.Note = "x";
        context.Attach(item);

        item.Title = "Second";
        item.Count = 4;
        item.Note = null;
        item.ReviewedAt = new DateTimeOffset(2026, 10, 28, 8, 0, 0, TimeSpan.FromHours(8));
        context.ChangeTracker.DetectChanges();

        var change = Extract(context).ShouldHaveSingleItem();

        change.Action.ShouldBe(EntityChangeAction.Updated);
        AssertChanges(change, """
            {
              "Title": { "old": "First", "new": "Second" },
              "Count": { "old": 3, "new": 4 },
              "Note": { "old": "x", "new": null },
              "ReviewedAt": { "old": null, "new": "2026-10-28T00:00:00Z" }
            }
            """);
    }

    [Fact]
    public void Deleted_entity_is_Deleted_with_old_values()
    {
        using var context = new AuditTestDbContext();
        var item = NewItem();
        item.Note = "kept";
        context.Attach(item);
        context.Remove(item);
        context.ChangeTracker.DetectChanges();

        var change = Extract(context).ShouldHaveSingleItem();

        change.Action.ShouldBe(EntityChangeAction.Deleted);
        change.EntityId.ShouldBe(ItemId.ToString());
        AssertChanges(change, """
            {
              "Id": { "old": "0a7c1b52-3d4e-4f60-8a91-b2c3d4e5f607" },
              "Title": { "old": "First" },
              "Count": { "old": 3 },
              "Due": { "old": "2026-10-28" },
              "State": { "old": "Draft" },
              "Flag": { "old": "None" },
              "Note": { "old": "kept" }
            }
            """);
    }

    [Fact]
    public void IsDeleted_false_to_true_is_SoftDeleted()
    {
        using var context = new AuditTestDbContext();
        var item = NewItem();
        context.Attach(item);
        item.IsDeleted = true;
        item.DeletedAt = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        item.DeletedBy = Guid.NewGuid();
        context.ChangeTracker.DetectChanges();

        var change = Extract(context).ShouldHaveSingleItem();

        change.Action.ShouldBe(EntityChangeAction.SoftDeleted);
        AssertChanges(change, "{}");
    }

    [Fact]
    public void IsDeleted_false_to_true_with_another_change_is_SoftDeleted_with_that_change()
    {
        using var context = new AuditTestDbContext();
        var item = NewItem();
        context.Attach(item);
        item.IsDeleted = true;
        item.Title = "Second";
        context.ChangeTracker.DetectChanges();

        var change = Extract(context).ShouldHaveSingleItem();

        change.Action.ShouldBe(EntityChangeAction.SoftDeleted);
        AssertChanges(change, """{ "Title": { "old": "First", "new": "Second" } }""");
    }

    [Fact]
    public void IsDeleted_true_to_false_is_Restored()
    {
        using var context = new AuditTestDbContext();
        var item = NewItem();
        item.IsDeleted = true;
        context.Attach(item);
        item.IsDeleted = false;
        context.ChangeTracker.DetectChanges();

        var change = Extract(context).ShouldHaveSingleItem();

        change.Action.ShouldBe(EntityChangeAction.Restored);
        AssertChanges(change, "{}");
    }

    [Fact]
    public void IsDeleted_true_to_false_with_another_change_is_Restored_with_that_change()
    {
        using var context = new AuditTestDbContext();
        var item = NewItem();
        item.IsDeleted = true;
        context.Attach(item);
        item.IsDeleted = false;
        item.Count = 9;
        context.ChangeTracker.DetectChanges();

        var change = Extract(context).ShouldHaveSingleItem();

        change.Action.ShouldBe(EntityChangeAction.Restored);
        AssertChanges(change, """{ "Count": { "old": 3, "new": 9 } }""");
    }

    [Fact]
    public void Masked_property_change_is_written_as_asterisks_on_both_sides_and_never_with_its_value()
    {
        using var context = new AuditTestDbContext();
        var item = NewItem();
        item.Secret = "hunter2-old";
        context.Attach(item);
        item.Secret = "hunter2-new";
        context.ChangeTracker.DetectChanges();

        var change = Extract(context).ShouldHaveSingleItem();

        AssertChanges(change, """{ "Secret": { "old": "****", "new": "****" } }""");
        change.Changes.ShouldNotContain("hunter2");
    }

    [Fact]
    public void Masked_property_on_a_new_entity_is_written_as_asterisks_on_the_new_side()
    {
        using var context = new AuditTestDbContext();
        var item = NewItem();
        item.Secret = "hunter2-new";
        context.Items.Add(item);
        context.ChangeTracker.DetectChanges();

        var change = Extract(context).ShouldHaveSingleItem();

        JsonNode.Parse(change.Changes)!["Secret"]!.ToJsonString().ShouldBe("""{"new":"****"}""");
        change.Changes.ShouldNotContain("hunter2");
    }

    [Fact]
    public void Ignored_property_never_appears()
    {
        using var context = new AuditTestDbContext();
        var item = NewItem();
        item.Ignored = "ignored-value";
        context.Items.Add(item);
        context.ChangeTracker.DetectChanges();

        var added = Extract(context).ShouldHaveSingleItem();

        added.Changes.ShouldNotContain("Ignored");
        added.Changes.ShouldNotContain("ignored-value");
    }

    [Fact]
    public void Converter_values_are_written()
    {
        using var context = new AuditTestDbContext();
        var item = NewItem();
        item.State = AuditedItemState.Submitted;
        item.Flag = AuditedItemFlag.Urgent;
        item.Due = new DateOnly(2026, 10, 28);
        context.Items.Add(item);
        context.ChangeTracker.DetectChanges();

        var changes = JsonNode.Parse(Extract(context).ShouldHaveSingleItem().Changes)!;

        changes["Due"]!["new"]!.GetValue<string>().ShouldBe("2026-10-28");
        changes["State"]!["new"]!.GetValue<string>().ShouldBe("Submitted");
        changes["Flag"]!["new"]!.GetValue<string>().ShouldBe("Urgent");
    }

    [Fact]
    public void Long_value_is_cut_to_4000_characters_ending_in_an_ellipsis()
    {
        using var context = new AuditTestDbContext();
        var item = NewItem();
        item.Title = new string('x', 5000);
        context.Items.Add(item);
        context.ChangeTracker.DetectChanges();

        var title = JsonNode.Parse(Extract(context).ShouldHaveSingleItem().Changes)!["Title"]!["new"]!.GetValue<string>();

        title.Length.ShouldBe(EntityChangeExtractor.MaxValueLength);
        title.ShouldEndWith("…");
        title.ShouldStartWith(new string('x', EntityChangeExtractor.MaxValueLength - 1));
    }

    [Fact]
    public void Value_of_exactly_4000_characters_is_kept()
    {
        using var context = new AuditTestDbContext();
        var item = NewItem();
        item.Title = new string('x', EntityChangeExtractor.MaxValueLength);
        context.Items.Add(item);
        context.ChangeTracker.DetectChanges();

        var title = JsonNode.Parse(Extract(context).ShouldHaveSingleItem().Changes)!["Title"]!["new"]!.GetValue<string>();

        title.ShouldBe(item.Title);
    }

    [Fact]
    public void Composite_key_is_written_as_Name_equals_value_joined_by_semicolons()
    {
        using var context = new AuditTestDbContext();
        var leftId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        context.Links.Add(new AuditedLink { LeftId = leftId, RightId = 1234567, Label = "link" });
        context.ChangeTracker.DetectChanges();

        var change = Extract(context).ShouldHaveSingleItem();

        change.EntityType.ShouldBe(nameof(AuditedLink));
        change.EntityId.ShouldBe($"LeftId={leftId};RightId=1234567");
    }

    [Fact]
    public void Single_key_is_the_plain_invariant_string()
    {
        using var context = new AuditTestDbContext();
        context.Items.Add(NewItem());
        context.ChangeTracker.DetectChanges();

        Extract(context).ShouldHaveSingleItem().EntityId.ShouldBe("0a7c1b52-3d4e-4f60-8a91-b2c3d4e5f607");
    }

    [Fact]
    public void Entities_without_the_attribute_are_not_visited()
    {
        using var context = new AuditTestDbContext();
        context.UnauditedItems.Add(new UnauditedItem { Id = Guid.NewGuid(), Name = "plain" });
        context.ChangeTracker.DetectChanges();

        Extract(context).ShouldBeEmpty();
    }

    [Fact]
    public void Unchanged_entities_are_not_visited()
    {
        using var context = new AuditTestDbContext();
        context.Attach(NewItem());
        context.ChangeTracker.DetectChanges();

        Extract(context).ShouldBeEmpty();
    }

    [Fact]
    public void A_save_that_changes_only_ignored_stamp_or_row_version_columns_returns_no_change()
    {
        using var context = new AuditTestDbContext();
        var item = NewItem();
        context.Attach(item);

        item.Ignored = "touched";
        item.UpdatedAt = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        item.UpdatedBy = Guid.NewGuid();
        item.RowVersion = [1, 2, 3];
        item.DeletedAt = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        item.DeletedBy = Guid.NewGuid();
        context.ChangeTracker.DetectChanges();

        Extract(context).ShouldBeEmpty();
    }

    [Fact]
    public void Entries_of_one_save_are_all_returned()
    {
        using var context = new AuditTestDbContext();
        context.Items.Add(NewItem());
        context.Links.Add(new AuditedLink { LeftId = Guid.NewGuid(), RightId = 2, Label = "link" });
        context.UnauditedItems.Add(new UnauditedItem { Id = Guid.NewGuid(), Name = "plain" });
        context.ChangeTracker.DetectChanges();

        Extract(context).Select(change => change.EntityType).ShouldBe([nameof(AuditedItem), nameof(AuditedLink)], ignoreOrder: true);
    }

    [Fact]
    public void Added_entity_with_a_temporary_store_key_throws_naming_the_type()
    {
        using var context = new AuditTestDbContext();
        context.Identities.Add(new AuditedIdentity { Name = "generated" });
        context.ChangeTracker.DetectChanges();

        var exception = Should.Throw<InvalidOperationException>(() => Extract(context));

        exception.Message.ShouldContain(nameof(AuditedIdentity));
    }

    private static AuditedItem NewItem() => new()
    {
        Id = ItemId,
        Title = "First",
        Count = 3,
        Due = new DateOnly(2026, 10, 28),
        CreatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedBy = Guid.NewGuid(),
    };

    private static IReadOnlyList<EntityChange> Extract(AuditTestDbContext context) => EntityChangeExtractor.Extract(context.ChangeTracker);

    private static void AssertChanges(EntityChange change, string expectedJson) =>
        JsonNode.DeepEquals(JsonNode.Parse(change.Changes), JsonNode.Parse(expectedJson))
            .ShouldBeTrue($"Expected {expectedJson} but was {change.Changes}");
}
