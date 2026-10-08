using System.Net;
using System.Text.Json;
using TemplateName.IntegrationTests.Infrastructure;

namespace TemplateName.IntegrationTests.Auth;

public sealed class JwksEndpointTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task Jwks_is_served_anonymously_at_the_root_with_public_parameters_only()
    {
        using var response = await Client.GetAsync("/.well-known/jwks.json", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        response.Headers.CacheControl!.Public.ShouldBeTrue();
        response.Headers.CacheControl.MaxAge.ShouldBe(TimeSpan.FromSeconds(300));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        // The Testing environment has no configured key, so the host made one ephemeral key at start.
        var key = document.RootElement.GetProperty("keys").EnumerateArray().ShouldHaveSingleItem();
        key.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ShouldBe(["alg", "crv", "kid", "kty", "use", "x", "y"]);
        key.GetProperty("kty").GetString().ShouldBe("EC");
        key.GetProperty("crv").GetString().ShouldBe("P-256");
        key.GetProperty("alg").GetString().ShouldBe("ES256");
        key.GetProperty("use").GetString().ShouldBe("sig");
    }

    [Fact]
    public async Task Jwks_is_not_under_the_api_group()
    {
        using var response = await Client.GetAsync("/api/v1/.well-known/jwks.json", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
