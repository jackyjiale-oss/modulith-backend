using TemplateName.ArchitectureTests;
using TemplateName.IntegrationTests.Infrastructure;

namespace TemplateName.IntegrationTests.Documentation;

/// <summary>
/// Pins the OpenAPI document (blueprint 12.1, contract snapshot; ADR 0011). An accidental API change fails here; an intended one is
/// accepted by replacing the <c>.verified.json</c> next to this file with the <c>.received.json</c> the failure writes.
/// </summary>
public sealed class OpenApiSnapshotTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task OpenApi_document_matches_snapshot()
    {
        using var response = await Client.GetAsync("/openapi/v1.json", Ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(Ct);

        // The servers list holds the test host's address, which differs between machines.
        var failure = JsonSnapshot.Compare(JsonSnapshot.Normalize(body, "servers"), VerifiedPath());

        failure.ShouldBeNull(failure);
    }

    /// <summary>The project folder is named after the assembly (docs/coding-conventions.md F6), so this works in generated projects too.</summary>
    private static string VerifiedPath()
        => Path.Combine(
            RepositoryPaths.Root,
            "tests",
            typeof(OpenApiSnapshotTests).Assembly.GetName().Name!,
            "Documentation",
            $"{nameof(OpenApiSnapshotTests)}.{nameof(OpenApi_document_matches_snapshot)}.verified.json");
}
