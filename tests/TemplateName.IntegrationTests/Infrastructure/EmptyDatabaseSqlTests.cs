namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>The statement text only; no SQL Server and no test host.</summary>
public sealed class EmptyDatabaseSqlTests
{
    [Theory]
    [InlineData("Plain_name", "Plain_name", "[Plain_name]")]
    [InlineData("Acme.Smoke_MigrateCommand", "Acme.Smoke_MigrateCommand", "[Acme.Smoke_MigrateCommand]")]
    [InlineData("Acme-Hr MigrateCommand", "Acme-Hr MigrateCommand", "[Acme-Hr MigrateCommand]")]
    [InlineData("a]b", "a]b", "[a]]b]")]
    [InlineData("it's", "it''s", "[it's]")]
    [InlineData("a.b-c d]e'f", "a.b-c d]e''f", "[a.b-c d]]e'f]")]
    public void Build_escapes_the_name_in_the_literal_and_in_every_bracketed_place(string name, string literal, string quoted)
        => EmptyDatabaseSql.Build(name).ShouldBe(
            $"IF DB_ID(N'{literal}') IS NOT NULL BEGIN ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {quoted}; END; CREATE DATABASE {quoted};");
}
