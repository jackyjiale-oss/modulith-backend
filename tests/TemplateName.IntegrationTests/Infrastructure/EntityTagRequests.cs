using System.Net.Http.Json;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>Sends an update with an optional <c>If-Match</c> (sent as given, so malformed values reach the server) and reads <c>ETag</c>.</summary>
internal static class EntityTagRequests
{
    public static async Task<HttpResponseMessage> PutJsonAsync(
        this HttpClient client, string route, object body, string? ifMatch, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, route) { Content = JsonContent.Create(body) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>The raw <c>ETag</c> header of a response, quotes included; fails the test when there is none.</summary>
    public static string ETagOf(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("ETag", out var values).ShouldBeTrue("the response has no ETag header");
        return values!.ShouldHaveSingleItem();
    }
}
