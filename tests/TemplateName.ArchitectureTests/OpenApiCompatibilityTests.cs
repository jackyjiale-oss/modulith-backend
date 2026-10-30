using System.Text.Json.Nodes;

namespace TemplateName.ArchitectureTests;

/// <summary>
/// The rules of the breaking-change gate (docs/api-compatibility.md), one test per rule, on minimal OpenAPI 3.1 documents shaped like the
/// one the host emits. No host, no Docker: this project has no assembly fixture.
/// </summary>
public sealed class OpenApiCompatibilityTests : IDisposable
{
    private const string Snapshot = "tests/TemplateName.IntegrationTests/Documentation/OpenApiSnapshotTests.OpenApi_document_matches_snapshot.verified.json";

    private const string Items = "/api/v1/items";

    private const string Get = "GET /api/v1/items";

    private const string Text = """{"type":"string"}""";

    private const string NoContent = """{"204":{"description":"No Content"}}""";

    private const string OneOfAOrB = """{"oneOf":[{"$ref":"#/components/schemas/A"},{"$ref":"#/components/schemas/B"}]}""";

    private readonly string _directory = Directory.CreateTempSubdirectory("openapi-baseline-").FullName;

    [Fact]
    public void Removed_operation_is_breaking()
    {
        var baseline = Document(PathsOf((Items, "get", Responding(Text)), (Items, "delete", Responding(Text))));
        var current = Document(PathsOf((Items, "get", Responding(Text))));

        var report = OpenApiCompatibility.Compare(baseline, current);

        report.Breaking.ShouldHaveSingleItem().ShouldBe(new OpenApiChange("DELETE /api/v1/items", "operation removed"));
        report.Compatible.ShouldBeEmpty();
    }

    [Fact]
    public void Removed_path_is_breaking()
    {
        var baseline = Document(PathsOf((Items, "get", Responding(Text)), ("/api/v1/orders", "get", Responding(Text))));
        var current = Document(PathsOf((Items, "get", Responding(Text))));

        var report = OpenApiCompatibility.Compare(baseline, current);

        report.Breaking.ShouldHaveSingleItem().ShouldBe(new OpenApiChange("GET /api/v1/orders", "path removed"));
    }

    [Fact]
    public void Added_required_request_parameter_is_breaking()
    {
        var report = OpenApiCompatibility.Compare(
            Document(Paths(WithParameters("[]"))),
            Document(Paths(WithParameters("""[{"name":"q","in":"query","required":true,"schema":{"type":"string"}}]"""))));

        report.Breaking.ShouldHaveSingleItem().ShouldBe(new OpenApiChange(Get, "required query parameter 'q' added"));
        report.Compatible.ShouldBeEmpty();
    }

    [Fact]
    public void Optional_request_parameter_made_required_is_breaking()
    {
        var report = OpenApiCompatibility.Compare(
            Document(Paths(WithParameters("""[{"name":"q","in":"query","schema":{"type":"string"}}]"""))),
            Document(Paths(WithParameters("""[{"name":"q","in":"query","required":true,"schema":{"type":"string"}}]"""))));

        report.Breaking.ShouldHaveSingleItem().ShouldBe(new OpenApiChange(Get, "query parameter 'q' made required"));
    }

    [Fact]
    public void Added_required_body_property_is_breaking()
    {
        var report = OpenApiCompatibility.Compare(
            Document(Paths(Accepting(ObjectOf("""{"name":{"type":"string"}}""", """["name"]""")))),
            Document(Paths(Accepting(ObjectOf("""{"name":{"type":"string"},"note":{"type":"string"}}""", """["name","note"]""")))));

        report.Breaking.ShouldHaveSingleItem().ShouldBe(new OpenApiChange(Get, "request body: required property 'note' added"));
        report.Compatible.ShouldBeEmpty();
    }

    [Fact]
    public void Optional_body_property_made_required_is_breaking()
    {
        var report = OpenApiCompatibility.Compare(
            Document(Paths(Accepting(ObjectOf("""{"name":{"type":"string"},"note":{"type":"string"}}""", """["name"]""")))),
            Document(Paths(Accepting(ObjectOf("""{"name":{"type":"string"},"note":{"type":"string"}}""", """["name","note"]""")))));

        report.Breaking.ShouldHaveSingleItem().ShouldBe(new OpenApiChange(Get, "request body: property 'note' made required"));
        report.Compatible.ShouldBeEmpty();
    }

    [Fact]
    public void Request_body_made_required_is_breaking()
    {
        var report = OpenApiCompatibility.Compare(
            Document(Paths(Accepting(Text, required: false))),
            Document(Paths(Accepting(Text, required: true))));

        report.Breaking.ShouldHaveSingleItem().ShouldBe(new OpenApiChange(Get, "request body made required"));
    }

    [Fact]
    public void Changed_property_type_is_breaking()
    {
        var request = OpenApiCompatibility.Compare(
            Document(Paths(Accepting(ObjectOf("""{"age":{"type":"integer"}}""")))),
            Document(Paths(Accepting(ObjectOf("""{"age":{"type":"string"}}""")))));
        var response = OpenApiCompatibility.Compare(
            Document(Paths(Responding(ObjectOf("""{"name":{"type":["string"]}}""", """["name"]""")))),
            Document(Paths(Responding(ObjectOf("""{"name":{"type":["string","null"]}}""", """["name"]""")))));

        request.Breaking.ShouldHaveSingleItem().ShouldBe(new OpenApiChange(Get, "request body: type of 'age' changed from integer to string"));
        response.Breaking.ShouldHaveSingleItem()
            .ShouldBe(new OpenApiChange(Get, "response 200: type of 'name' changed from string to null|string"));
    }

    [Fact]
    public void Changed_property_format_is_breaking()
    {
        var report = OpenApiCompatibility.Compare(
            Document(Paths(Responding(ObjectOf("""{"on":{"type":"string","format":"date"}}""", """["on"]""")))),
            Document(Paths(Responding(ObjectOf("""{"on":{"type":"string","format":"date-time"}}""", """["on"]""")))));

        report.Breaking.ShouldHaveSingleItem().ShouldBe(new OpenApiChange(Get, "response 200: format of 'on' changed from date to date-time"));
    }

    [Fact]
    public void Removed_response_property_is_breaking()
    {
        var report = OpenApiCompatibility.Compare(
            Document(Paths(Responding(ObjectOf("""{"id":{"type":"string"},"name":{"type":"string"}}""")))),
            Document(Paths(Responding(ObjectOf("""{"id":{"type":"string"}}""")))));

        report.Breaking.ShouldHaveSingleItem().ShouldBe(new OpenApiChange(Get, "response 200: property 'name' removed"));
        report.Compatible.ShouldBeEmpty();
    }

    [Fact]
    public void Response_property_no_longer_required_is_breaking()
    {
        var report = OpenApiCompatibility.Compare(
            Document(Paths(Responding(ObjectOf("""{"name":{"type":"string"}}""", """["name"]""")))),
            Document(Paths(Responding(ObjectOf("""{"name":{"type":"string"}}""")))));

        report.Breaking.ShouldHaveSingleItem().ShouldBe(new OpenApiChange(Get, "response 200: property 'name' no longer required"));
    }

    [Fact]
    public void Removed_success_status_code_is_breaking()
    {
        var baseline = Document(Paths("""{"responses":{"200":{"description":"OK"},"201":{"description":"Created"}}}"""));
        var current = Document(Paths("""{"responses":{"201":{"description":"Created"}}}"""));

        var report = OpenApiCompatibility.Compare(baseline, current);

        report.Breaking.ShouldHaveSingleItem().ShouldBe(new OpenApiChange(Get, "success status 200 removed"));
    }

    [Fact]
    public void Removed_request_enum_value_is_breaking()
    {
        var report = OpenApiCompatibility.Compare(
            Document(Paths(Accepting(ObjectOf("""{"status":{"type":"string","enum":["Open","Closed"]}}""")))),
            Document(Paths(Accepting(ObjectOf("""{"status":{"type":"string","enum":["Open"]}}""")))));

        report.Breaking.ShouldHaveSingleItem()
            .ShouldBe(new OpenApiChange(Get, "request body: value \"Closed\" removed from the enum of 'status'"));
    }

    [Fact]
    public void Security_added_to_an_open_operation_is_breaking()
    {
        var baseline = Document(Paths("""{"responses":{"204":{"description":"No Content"}}}"""));
        var current = Document(Paths("""{"responses":{"204":{"description":"No Content"}},"security":[{"Bearer":[]}]}"""));

        var report = OpenApiCompatibility.Compare(baseline, current);

        report.Breaking.ShouldHaveSingleItem().ShouldBe(new OpenApiChange(Get, "security requirement added"));
    }

    [Fact]
    public void Removed_property_inside_a_referenced_array_item_is_breaking()
    {
        var baseline = Document(
            Paths(Responding(Ref("Page"))),
            """
            {"Page":{"type":"object","properties":{"items":{"type":"array","items":{"$ref":"#/components/schemas/Item"}}}},
             "Item":{"type":"object","properties":{"id":{"type":"string"},"name":{"type":"string"}}}}
            """);
        var current = Document(
            Paths(Responding(Ref("Page"))),
            """
            {"Page":{"type":"object","properties":{"items":{"type":"array","items":{"$ref":"#/components/schemas/Item"}}}},
             "Item":{"type":"object","properties":{"id":{"type":"string"}}}}
            """);

        var report = OpenApiCompatibility.Compare(baseline, current);

        report.Breaking.ShouldHaveSingleItem().ShouldBe(new OpenApiChange(Get, "response 200: property 'items[].name' removed"));
    }

    [Fact]
    public void Allof_members_are_merged_before_comparing()
    {
        const string Composed = """{"allOf":[{"$ref":"#/components/schemas/Base"},{"required":["name"],"properties":{"name":{"type":"string"}}}]}""";
        var inline = Document(Paths(Responding(ObjectOf("""{"id":{"type":"string"},"name":{"type":"string"}}""", """["id","name"]"""))));
        var composed = Document(
            Paths(Responding(Composed)),
            """{"Base":{"type":"object","required":["id"],"properties":{"id":{"type":"string"}}}}""");
        var baseShrunk = Document(Paths(Responding(Composed)), """{"Base":{"type":"object","properties":{}}}""");

        var restructured = OpenApiCompatibility.Compare(inline, composed);
        var shrunk = OpenApiCompatibility.Compare(composed, baseShrunk);

        restructured.Breaking.ShouldBeEmpty();
        restructured.Compatible.ShouldBeEmpty();
        shrunk.Breaking.ShouldHaveSingleItem().ShouldBe(new OpenApiChange(Get, "response 200: property 'id' removed"));
    }

    [Fact]
    public void Changed_oneof_is_breaking()
    {
        var report = OpenApiCompatibility.Compare(
            Document(Paths(Accepting("""{"oneOf":[{"type":"null"},{"type":"string"}]}"""))),
            Document(Paths(Accepting("""{"oneOf":[{"type":"null"},{"type":"integer"}]}"""))));

        report.Breaking.ShouldHaveSingleItem().ShouldBe(new OpenApiChange(Get, "request body: oneOf changed"));
    }

    [Fact]
    public void Oneof_members_are_compared_through_their_refs()
    {
        var report = OpenApiCompatibility.Compare(
            Document(Paths(Responding(OneOfAOrB)), SchemasAAndB("""{"id":{"type":"string"},"name":{"type":"string"}}""")),
            Document(Paths(Responding(OneOfAOrB)), SchemasAAndB("""{"id":{"type":"string"}}""")));

        report.Breaking.ShouldHaveSingleItem().ShouldBe(new OpenApiChange(Get, "response 200: property 'name' removed"));
        report.Compatible.ShouldBeEmpty();
    }

    [Fact]
    public void Oneof_member_added_property_is_compatible()
    {
        var report = OpenApiCompatibility.Compare(
            Document(Paths(Responding(OneOfAOrB)), SchemasAAndB("""{"id":{"type":"string"}}""")),
            Document(Paths(Responding(OneOfAOrB)), SchemasAAndB("""{"id":{"type":"string"},"name":{"type":"string"}}""")));

        report.Breaking.ShouldBeEmpty();
        report.Compatible.ShouldHaveSingleItem().ShouldBe(new OpenApiChange(Get, "response 200: property 'name' added"));
    }

    [Fact]
    public void Added_operation_is_compatible()
    {
        var baseline = Document(PathsOf((Items, "get", Responding(Text))));
        var current = Document(PathsOf((Items, "get", Responding(Text)), (Items, "post", Accepting(Text))));

        var report = OpenApiCompatibility.Compare(baseline, current);

        report.Breaking.ShouldBeEmpty();
        report.Compatible.ShouldHaveSingleItem().ShouldBe(new OpenApiChange("POST /api/v1/items", "operation added"));
    }

    [Fact]
    public void Added_optional_request_parameter_or_property_is_compatible()
    {
        var parameter = OpenApiCompatibility.Compare(
            Document(Paths(WithParameters("[]"))),
            Document(Paths(WithParameters("""[{"name":"q","in":"query","schema":{"type":"string"}}]"""))));
        var property = OpenApiCompatibility.Compare(
            Document(Paths(Accepting(ObjectOf("""{"name":{"type":"string"}}""")))),
            Document(Paths(Accepting(ObjectOf("""{"name":{"type":"string"},"note":{"type":["null","string"]}}""")))));

        parameter.Breaking.ShouldBeEmpty();
        parameter.Compatible.ShouldHaveSingleItem().ShouldBe(new OpenApiChange(Get, "optional query parameter 'q' added"));
        property.Breaking.ShouldBeEmpty();
        property.Compatible.ShouldHaveSingleItem().ShouldBe(new OpenApiChange(Get, "request body: optional property 'note' added"));
    }

    [Fact]
    public void Added_response_property_is_compatible()
    {
        var report = OpenApiCompatibility.Compare(
            Document(Paths(Responding(ObjectOf("""{"id":{"type":"string"}}""", """["id"]""")))),
            Document(Paths(Responding(ObjectOf("""{"id":{"type":"string"},"name":{"type":"string"}}""", """["id","name"]""")))));

        report.Breaking.ShouldBeEmpty();
        report.Compatible.ShouldHaveSingleItem().ShouldBe(new OpenApiChange(Get, "response 200: property 'name' added"));
    }

    [Fact]
    public void Added_response_enum_value_is_compatible()
    {
        var report = OpenApiCompatibility.Compare(
            Document(Paths(Responding(ObjectOf("""{"status":{"type":"string","enum":["Open"]}}""")))),
            Document(Paths(Responding(ObjectOf("""{"status":{"type":"string","enum":["Open","Closed"]}}""")))));

        report.Breaking.ShouldBeEmpty();
        report.Compatible.ShouldHaveSingleItem()
            .ShouldBe(new OpenApiChange(Get, "response 200: value \"Closed\" added to the enum of 'status'"));
    }

    [Fact]
    public void Removed_non_success_response_is_ignored()
    {
        var baseline = Document(Paths("""{"responses":{"204":{"description":"No Content"},"404":{"description":"Not Found"}}}"""));
        var current = Document(Paths("""{"responses":{"204":{"description":"No Content"}}}"""));

        var report = OpenApiCompatibility.Compare(baseline, current);

        report.Breaking.ShouldBeEmpty();
        report.Compatible.ShouldBeEmpty();
    }

    /// <summary>The committed snapshot, so the classifier is proven quiet on the document the host really emits.</summary>
    [Fact]
    public void Identical_documents_have_no_changes()
    {
        var snapshot = File.ReadAllText(Path.Combine(RepositoryPaths.Root, Snapshot));

        var report = OpenApiCompatibility.Compare(snapshot, snapshot);

        report.Breaking.ShouldBeEmpty();
        report.Compatible.ShouldBeEmpty();
    }

    [Fact]
    public void Recursive_schema_terminates()
    {
        var full = Document(
            Paths(Responding(Ref("Node"))),
            """
            {"Node":{"type":"object","properties":{
              "name":{"type":"string"},
              "children":{"type":"array","items":{"$ref":"#/components/schemas/Node"}}}}}
            """);
        var shrunk = Document(
            Paths(Responding(Ref("Node"))),
            """
            {"Node":{"type":"object","properties":{
              "children":{"type":"array","items":{"$ref":"#/components/schemas/Node"}}}}}
            """);

        var identical = OpenApiCompatibility.Compare(full, full);
        var removed = OpenApiCompatibility.Compare(full, shrunk);

        identical.Breaking.ShouldBeEmpty();
        identical.Compatible.ShouldBeEmpty();
        removed.Breaking.ShouldHaveSingleItem().ShouldBe(new OpenApiChange(Get, "response 200: property 'name' removed"));
    }

    [Fact]
    public void Verdict_is_null_without_breaking_changes()
    {
        var report = new OpenApiCompatibilityReport([], [new OpenApiChange("POST /api/v1/items", "operation added")]);

        OpenApiCompatibility.Verdict(report, breakingAccepted: false).ShouldBeNull();
    }

    [Fact]
    public void Verdict_is_null_when_the_break_is_accepted()
    {
        var report = new OpenApiCompatibilityReport([new OpenApiChange(Get, "response 200: property 'name' removed")], []);

        OpenApiCompatibility.Verdict(report, breakingAccepted: true).ShouldBeNull();
    }

    [Fact]
    public void Verdict_names_each_breaking_change_and_the_ways_out()
    {
        var report = new OpenApiCompatibilityReport(
            [new OpenApiChange(Get, "response 200: property 'name' removed"), new OpenApiChange("DELETE /api/v1/items", "operation removed")],
            []);

        var verdict = OpenApiCompatibility.Verdict(report, breakingAccepted: false);

        verdict.ShouldNotBeNull();
        verdict.ShouldContain("GET /api/v1/items: response 200: property 'name' removed");
        verdict.ShouldContain("DELETE /api/v1/items: operation removed");
        verdict.ShouldContain("feat(api)!:");
        verdict.ShouldContain("docs/api-compatibility.md");
    }

    [Fact]
    public void ReadBaseline_returns_the_text_of_a_json_object()
    {
        var path = Path.Combine(_directory, "baseline.json");
        File.WriteAllText(path, """{"openapi":"3.1.1"}""");

        OpenApiCompatibility.ReadBaseline(path).ShouldBe("""{"openapi":"3.1.1"}""");
    }

    [Theory]
    [InlineData("404: Not Found")]
    [InlineData("[]")]
    public void Baseline_that_is_not_json_names_the_file(string text)
    {
        var path = Path.Combine(_directory, "baseline.json");
        File.WriteAllText(path, text);

        var exception = Should.Throw<InvalidOperationException>(() => OpenApiCompatibility.ReadBaseline(path));

        exception.Message.ShouldContain(path);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string Document(string paths, string schemas = "{}")
        => Fill("""{"openapi":"3.1.1","paths":@paths,"components":{"schemas":@schemas}}""", ("@paths", paths), ("@schemas", schemas));

    /// <summary>One path, <c>/api/v1/items</c>, with one GET operation.</summary>
    private static string Paths(string operation) => PathsOf((Items, "get", operation));

    private static string PathsOf(params (string Path, string Method, string Operation)[] operations)
    {
        var paths = new JsonObject();
        foreach (var (path, method, operation) in operations)
        {
            if (paths[path] is not JsonObject item)
            {
                item = [];
                paths[path] = item;
            }

            item[method] = JsonNode.Parse(operation);
        }

        return paths.ToJsonString();
    }

    private static string Responding(string schema)
        => Fill("""{"responses":{"200":{"description":"OK","content":{"application/json":{"schema":@schema}}}}}""", ("@schema", schema));

    private static string Accepting(string schema, bool required = true)
        => Fill(
            """{"requestBody":{"content":{"application/json":{"schema":@schema}},"required":@required},"responses":@noContent}""",
            ("@schema", schema),
            ("@required", required ? "true" : "false"),
            ("@noContent", NoContent));

    private static string WithParameters(string parameters)
        => Fill("""{"parameters":@parameters,"responses":@noContent}""", ("@parameters", parameters), ("@noContent", NoContent));

    private static string ObjectOf(string properties, string required = "[]")
        => Fill("""{"type":"object","required":@required,"properties":@properties}""", ("@required", required), ("@properties", properties));

    /// <summary>Schema <c>A</c> with <paramref name="propertiesOfA"/>, and an unchanging schema <c>B</c>.</summary>
    private static string SchemasAAndB(string propertiesOfA)
        => Fill(
            """{"A":{"type":"object","properties":@a},"B":{"type":"object","properties":{"code":{"type":"integer"}}}}""",
            ("@a", propertiesOfA));

    private static string Ref(string schema) => Fill("""{"$ref":"#/components/schemas/@name"}""", ("@name", schema));

    private static string Fill(string template, params (string Placeholder, string Value)[] values)
        => values.Aggregate(template, (text, value) => text.Replace(value.Placeholder, value.Value, StringComparison.Ordinal));
}
