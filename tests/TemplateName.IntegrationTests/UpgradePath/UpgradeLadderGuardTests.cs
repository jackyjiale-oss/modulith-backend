using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using TemplateName.Infrastructure.Common.Persistence;

namespace TemplateName.IntegrationTests.UpgradePath;

/// <summary>The rules of the upgrade-path ladder over synthetic migrations and over the real ones. They take no container.</summary>
public sealed class UpgradeLadderGuardTests
{
    [Fact]
    public void A_seed_for_a_missing_migration_is_reported()
    {
        var violations = UpgradeLadderRules.Violations(
            [Ladder(Rung("20260101000000_AddNickname", typeof(AddsNickname)))],
            Keys("Things/AddNickname", "Things/RenamedAway"),
            Keys("Things/AddNickname", "Things/RenamedAway"));

        var violation = violations.ShouldHaveSingleItem();
        violation.ShouldContain("Things/RenamedAway");
        violation.ShouldContain("no migration");
    }

    [Fact]
    public void A_seed_without_a_verify_and_a_verify_without_a_seed_are_reported()
    {
        var violations = UpgradeLadderRules.Violations(
            [Ladder(Rung("20260101000000_AddNickname", typeof(AddsNickname)), Rung("20260102000000_AddAge", typeof(AddsAge)))],
            Keys("Things/AddNickname"),
            Keys("Things/AddAge"));

        violations.Count.ShouldBe(2);
        violations.ShouldContain(violation => violation.Contains("Things/AddNickname", StringComparison.Ordinal) && violation.Contains("verify", StringComparison.Ordinal));
        violations.ShouldContain(violation => violation.Contains("Things/AddAge", StringComparison.Ordinal) && violation.Contains("seed", StringComparison.Ordinal));
    }

    [Fact]
    public void A_contract_migration_without_a_seed_before_it_is_reported()
    {
        var violations = UpgradeLadderRules.Violations(
            [Ladder(Rung("20260101000000_AddNickname", typeof(AddsNickname)), Rung("20260102000000_DropNickname", typeof(DropsNickname)))],
            Keys(),
            Keys());

        var violation = violations.ShouldHaveSingleItem();
        violation.ShouldContain("Things/DropNickname");
        violation.ShouldContain("Things/AddNickname");
    }

    [Fact]
    public void A_ladder_that_follows_the_rules_has_no_violations()
    {
        var violations = UpgradeLadderRules.Violations(
            [Ladder(Rung("20260101000000_AddNickname", typeof(AddsNickname)), Rung("20260102000000_DropNickname", typeof(DropsNickname)))],
            Keys("Things/AddNickname"),
            Keys("Things/AddNickname"));

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void The_real_ladder_has_no_violations()
    {
        var violations = UpgradeLadderRules.Violations(
            UpgradeLadder.Discover(),
            UpgradeLadder.Seeds.Keys.ToHashSet(StringComparer.Ordinal),
            UpgradeLadder.Verifies.Keys.ToHashSet(StringComparer.Ordinal));

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Discover_finds_the_auth_migrations_in_order()
    {
        var auth = UpgradeLadder.Discover().Single(ladder => ladder.Module == "Auth");

        auth.Rungs[0].Name.ShouldBe("InitialAuth");
        auth.Rungs[^1].Name.ShouldBe("AddPhoneCodePurpose");
        auth.Rungs.Select(rung => rung.Id).ShouldBe(auth.Rungs.Select(rung => rung.Id).Order(StringComparer.Ordinal));
        auth.Rungs.ShouldAllBe(rung => rung.Id.EndsWith("_" + rung.Name, StringComparison.Ordinal));
    }

    private static HashSet<string> Keys(params string[] keys) => keys.ToHashSet(StringComparer.Ordinal);

    private static MigrationRung Rung(string id, Type migrationType) => new(id, id[(id.IndexOf('_', StringComparison.Ordinal) + 1)..], migrationType);

    private static ModuleLadder Ladder(params MigrationRung[] rungs) => new("Things", typeof(DbContext), rungs);

    internal sealed class AddsNickname : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.AddColumn<string>("Nickname", "Things", nullable: true);
    }

    internal sealed class AddsAge : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<int>("Age", "Things", nullable: true);
    }

    [ContractMigration("2026-11 contract of Nickname")]
    internal sealed class DropsNickname : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn("Nickname", "Things");
    }
}
