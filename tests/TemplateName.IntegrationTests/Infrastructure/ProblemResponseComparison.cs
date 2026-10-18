using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// Compares refusals that must not tell their causes apart (account enumeration): the same status, the same headers but the trace id
/// and the date, and the same body members with the same values but <c>traceId</c>, which must equal each response's own
/// <c>X-Trace-Id</c>. Used by the login tests of Plan 2 and the two-step verification tests of Plan 4.
/// </summary>
internal static class ProblemResponseComparison
{
    private static readonly string[] VolatileHeaders = ["X-Trace-Id", "Date"];

    /// <summary>
    /// Asserts that <paramref name="first"/> is a <paramref name="status"/> problem with <paramref name="code"/> and that every other
    /// response is identical to it but for the trace id.
    /// </summary>
    public static async Task AssertIdenticalAsync(HttpStatusCode status, string code, HttpResponseMessage first, params HttpResponseMessage[] others)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(others);
        var cancellationToken = TestContext.Current.CancellationToken;

        first.StatusCode.ShouldBe(status);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        firstBody.GetProperty("code").GetString().ShouldBe(code);
        firstBody.GetProperty("traceId").GetString().ShouldBe(TraceId(first));

        foreach (var other in others)
        {
            other.StatusCode.ShouldBe(first.StatusCode);
            Headers(other).ShouldBe(Headers(first));

            var otherBody = await other.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            otherBody.GetProperty("traceId").GetString().ShouldBe(TraceId(other));
            PropertyNames(otherBody).ShouldBe(PropertyNames(firstBody));
            foreach (var name in PropertyNames(firstBody).Where(name => name != "traceId"))
            {
                otherBody.GetProperty(name).GetRawText().ShouldBe(firstBody.GetProperty(name).GetRawText(), name);
            }
        }
    }

    /// <summary>The response and content headers but the trace id and the date, by name ignoring case, values joined by <c>", "</c>.</summary>
    public static Dictionary<string, string> Headers(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Headers.Concat(response.Content.Headers)
            .Where(header => !VolatileHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase);
    }

    private static string TraceId(HttpResponseMessage response) => response.Headers.GetValues("X-Trace-Id").Single();

    private static List<string> PropertyNames(JsonElement body) => [.. body.EnumerateObject().Select(property => property.Name)];
}
