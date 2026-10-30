namespace TemplateName.ArchitectureTests;

public sealed class ApiVersionRegisterTests
{
    private const string Page = "api-compatibility.md";
    private const string Snapshot = "tests/TemplateName.IntegrationTests/Documentation/OpenApiSnapshotTests.OpenApi_document_matches_snapshot.verified.json";

    [Fact]
    public void Parse_reads_the_register_rows()
    {
        const string Markdown = """
            # Page

            | Version | Status |
            |---|---|
            | not | in the register |

            ## Version register

            Some words before the table.

            | Version | Status | Since | Owner | Retirement criteria |
            |---|:---:|---|---|---|
            | V1 |   Active   | 2026-10-07 | The template's maintainers | None planned |
            |  v2  | Deprecated | 2027-01-01 | Team | Usage at zero |

            ## Retiring a version

            | Version | Status |
            |---|---|
            | v3 | Active |
            """;

        var rows = ApiVersionRegister.Parse(Markdown);

        rows.ShouldBe([("v1", "Active"), ("v2", "Deprecated")]);
    }

    [Fact]
    public void Path_versions_are_the_distinct_api_prefixes()
    {
        const string OpenApi = """
            { "paths": { "/api/v1/a": {}, "/api/v1/b": {}, "/health": {}, "/apiary/v3/x": {} } }
            """;

        ApiVersionRegister.PathVersions(OpenApi).ShouldBe(["v1"], ignoreOrder: true);
    }

    [Fact]
    public void Path_versions_count_a_bare_version_and_not_a_longer_segment()
    {
        const string OpenApi = """
            { "paths": { "/api/v2": {}, "/api/v3x/a": {}, "/api/v4.1/a": {}, "/api/v5/b": {} } }
            """;

        ApiVersionRegister.PathVersions(OpenApi).ShouldBe(["v2", "v5"], ignoreOrder: true);
    }

    [Fact]
    public void Violations_flag_a_served_version_without_a_row()
    {
        var violations = ApiVersionRegister.Violations([("v1", "Active")], new HashSet<string> { "v1", "v2" });

        violations.ShouldHaveSingleItem().ShouldContain("v2");
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("Deprecated")]
    public void Violations_flag_an_active_or_deprecated_row_that_is_not_served(string status)
    {
        var violations = ApiVersionRegister.Violations([("v1", "Active"), ("v2", status)], new HashSet<string> { "v1" });

        var violation = violations.ShouldHaveSingleItem();
        violation.ShouldContain("v2");
        violation.ShouldContain(status);
    }

    [Theory]
    [InlineData("Retired")]
    [InlineData("Retirement announced")]
    public void Violations_accept_a_retired_or_announced_row_that_is_not_served(string status)
    {
        ApiVersionRegister.Violations([("v1", "Active"), ("v0", status)], new HashSet<string> { "v1" }).ShouldBeEmpty();
    }

    [Fact]
    public void Violations_flag_a_retired_row_that_is_still_served()
    {
        var violations = ApiVersionRegister.Violations([("v1", "Retired")], new HashSet<string> { "v1" });

        violations.ShouldHaveSingleItem().ShouldContain("Retired");
    }

    [Fact]
    public void Violations_flag_an_unknown_status()
    {
        var violations = ApiVersionRegister.Violations([("v1", "Soonish")], new HashSet<string> { "v1" });

        violations.ShouldHaveSingleItem().ShouldContain("Soonish");
    }

    [Fact]
    public void Register_matches_the_committed_openapi_snapshot()
    {
        var register = ApiVersionRegister.Parse(File.ReadAllText(Path.Combine(DocumentPaths.DocsDirectory, Page)));
        var served = ApiVersionRegister.PathVersions(File.ReadAllText(Path.Combine(RepositoryPaths.Root, Snapshot)));

        register.ShouldNotBeEmpty();
        served.ShouldNotBeEmpty();
        ApiVersionRegister.Violations(register, served).ShouldBeEmpty();
    }
}
