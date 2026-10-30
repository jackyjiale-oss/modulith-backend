using TemplateName.ArchitectureTests;
using TemplateName.IntegrationTests.Infrastructure;

namespace TemplateName.IntegrationTests.Documentation;

/// <summary>
/// The breaking-change gate (docs/api-compatibility.md): compares the current OpenAPI document with the base branch's verified snapshot,
/// which CI names in <c>OPENAPI_BASELINE</c> on a pull request. Without a baseline the test skips, so a local run is unaffected. A
/// pull-request title marked with <c>!</c> sets <c>API_BREAKING_ACCEPTED=true</c>: the breaking changes are then printed, not failed.
/// </summary>
public sealed class OpenApiBaselineTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task OpenApi_changes_are_compatible_with_the_baseline()
    {
        var baselinePath = Environment.GetEnvironmentVariable(OpenApiCompatibility.BaselineVariable);
        if (string.IsNullOrEmpty(baselinePath) || !File.Exists(baselinePath))
        {
            Assert.Skip("No OpenAPI baseline: set OPENAPI_BASELINE to a verified snapshot from the base branch.");
        }

        var baseline = OpenApiCompatibility.ReadBaseline(baselinePath);
        using var response = await Client.GetAsync("/openapi/v1.json", Ct);
        response.EnsureSuccessStatusCode();
        var current = JsonSnapshot.Normalize(await response.Content.ReadAsStringAsync(Ct), "servers");

        var report = OpenApiCompatibility.Compare(baseline, current);
        var isAccepted = string.Equals(
            Environment.GetEnvironmentVariable(OpenApiCompatibility.AcceptedVariable),
            "true",
            StringComparison.Ordinal);
        foreach (var change in report.Breaking)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"Breaking{(isAccepted ? " (accepted)" : "")}: {change.Operation}: {change.Description}");
        }

        foreach (var change in report.Compatible)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"Compatible: {change.Operation}: {change.Description}");
        }

        var verdict = OpenApiCompatibility.Verdict(report, breakingAccepted: isAccepted);
        verdict.ShouldBeNull(verdict);
    }
}
