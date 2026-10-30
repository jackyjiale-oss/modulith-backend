using Microsoft.EntityFrameworkCore.Migrations;
using TemplateName.Infrastructure.Common.Persistence;

namespace TemplateName.ArchitectureTests;

public sealed class MigrationTests
{
    [Fact]
    public void Guard_flags_a_dropped_column_without_the_marker()
    {
        var violations = MigrationGuard.Violations([typeof(DropsColumn)]);

        var violation = violations.ShouldHaveSingleItem();
        violation.ShouldContain("DropColumnOperation");
        violation.ShouldContain("[ContractMigration]");
    }

    [Fact]
    public void Guard_flags_a_renamed_table_without_the_marker()
    {
        var violations = MigrationGuard.Violations([typeof(RenamesTable)]);

        violations.ShouldHaveSingleItem().ShouldContain("RenameTableOperation");
    }

    [Fact]
    public void Guard_accepts_a_marked_contraction_and_an_additive_migration()
    {
        MigrationGuard.Violations([typeof(DropsColumnMarked), typeof(AddsColumnOnly)]).ShouldBeEmpty();
    }

    [Fact]
    public void Guard_flags_a_marker_on_a_migration_that_contracts_nothing()
    {
        var violations = MigrationGuard.Violations([typeof(MarkedButAddsOnly)]);

        violations.ShouldHaveSingleItem().ShouldContain("drops or renames no column or table");
    }

    [Fact]
    public void Guard_names_a_migration_it_cannot_read()
    {
        var violations = MigrationGuard.Violations([typeof(Throws)]);

        var violation = violations.ShouldHaveSingleItem();
        violation.ShouldContain(typeof(Throws).FullName!);
        violation.ShouldContain("boom");
    }

    [Fact]
    public void Guard_names_the_cause_when_a_migration_cannot_be_created()
    {
        var violations = MigrationGuard.Violations([typeof(ThrowsInConstructor)]);

        var violation = violations.ShouldHaveSingleItem();
        violation.ShouldContain(typeof(ThrowsInConstructor).FullName!);
        violation.ShouldContain("ctor boom");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Attribute_requires_a_reason(string? reason)
    {
        Should.Throw<ArgumentException>(() => new ContractMigrationAttribute(reason!));
    }

    [Fact]
    public void Every_migration_in_the_repository_is_additive_or_marked()
    {
        var migrations = Assemblies.All
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.IsSubclassOf(typeof(Migration)))
            .ToList();
        migrations.ShouldNotBeEmpty();

        var violations = MigrationGuard.Violations(migrations);

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    internal sealed class DropsColumn : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn("Name", "Things");
    }

    [ContractMigration("2026-11 contract of Name")]
    internal sealed class DropsColumnMarked : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn("Name", "Things");
    }

    internal sealed class RenamesTable : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.RenameTable("Things", newName: "Stuff");
    }

    internal sealed class AddsColumnOnly : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.AddColumn<string>("Nickname", "Things", nullable: true);
    }

    [ContractMigration("nothing to contract")]
    internal sealed class MarkedButAddsOnly : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.AddColumn<string>("Nickname", "Things", nullable: true);
    }

    internal sealed class Throws : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => throw new InvalidOperationException("boom");
    }

    internal sealed class ThrowsInConstructor : Migration
    {
        public ThrowsInConstructor() => throw new InvalidOperationException("ctor boom");

        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.AddColumn<string>("Nickname", "Things", nullable: true);
    }
}
