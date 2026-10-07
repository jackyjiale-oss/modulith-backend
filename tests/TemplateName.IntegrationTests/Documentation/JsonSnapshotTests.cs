namespace TemplateName.IntegrationTests.Documentation;

/// <summary>Proves the snapshot gate cannot pass silently: a different document fails and leaves a received file behind.</summary>
public sealed class JsonSnapshotTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("json-snapshot-").FullName;

    private string VerifiedPath => Path.Combine(_directory, "Sample.verified.json");

    private string ReceivedPath => Path.Combine(_directory, "Sample.received.json");

    [Fact]
    public void Normalize_removes_ignored_top_level_members_keeps_order_and_uses_lf()
    {
        var normalized = JsonSnapshot.Normalize("{\r\n\"b\":1,\"servers\":[{\"url\":\"http://localhost\"}],\"a\":{\"servers\":2}}", "servers");

        normalized.ShouldBe("{\n  \"b\": 1,\n  \"a\": {\n    \"servers\": 2\n  }\n}\n");
    }

    [Fact]
    public void Matching_document_passes_and_removes_a_stale_received_file()
    {
        File.WriteAllText(VerifiedPath, JsonSnapshot.Normalize("""{"a":1}""").ReplaceLineEndings("\r\n"));
        File.WriteAllText(ReceivedPath, "stale");

        var failure = JsonSnapshot.Compare(JsonSnapshot.Normalize("""{ "a": 1 }"""), VerifiedPath);

        failure.ShouldBeNull();
        File.Exists(ReceivedPath).ShouldBeFalse();
    }

    [Fact]
    public void Different_document_fails_and_writes_the_received_file()
    {
        File.WriteAllText(VerifiedPath, JsonSnapshot.Normalize("""{"a":1}"""));
        var actual = JsonSnapshot.Normalize("""{"a":2}""");

        var failure = JsonSnapshot.Compare(actual, VerifiedPath);

        failure.ShouldNotBeNull();
        failure.ShouldContain(VerifiedPath);
        failure.ShouldContain(ReceivedPath);
        File.ReadAllText(ReceivedPath).ShouldBe(actual);
        File.ReadAllText(VerifiedPath).ShouldBe(JsonSnapshot.Normalize("""{"a":1}"""));
    }

    [Fact]
    public void Missing_snapshot_fails_and_writes_the_received_file()
    {
        var failure = JsonSnapshot.Compare(JsonSnapshot.Normalize("""{"a":1}"""), VerifiedPath);

        failure.ShouldNotBeNull();
        failure.ShouldContain("does not exist");
        File.Exists(ReceivedPath).ShouldBeTrue();
    }

    [Fact]
    public void Snapshot_path_without_verified_is_rejected()
    {
        Should.Throw<ArgumentException>(() => JsonSnapshot.Compare("{}\n", Path.Combine(_directory, "Sample.json")));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
