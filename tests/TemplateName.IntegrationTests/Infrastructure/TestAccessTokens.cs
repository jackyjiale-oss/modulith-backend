using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Modules.Auth.Application.Abstractions;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// Access tokens minted by the host's own <see cref="IAccessTokenIssuer"/>, for tests that need a signed-in caller without a login.
/// Each host has its own (ephemeral) signing key, so mint from the services of the host that will receive the token.
/// </summary>
internal static class TestAccessTokens
{
    internal const string SecurityStamp = "TEST-SECURITY-STAMP";

    /// <summary>A valid token for <paramref name="userId"/> (a new id when null) and session, issued now by the host's clock.</summary>
    internal static AccessToken Issue(IServiceProvider services, Guid? userId = null, Guid? sessionId = null, string locale = "en")
    {
        var issuedAt = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var request = new AccessTokenRequest(userId ?? Guid.NewGuid(), sessionId ?? Guid.NewGuid(), SecurityStamp, "pwd", issuedAt, locale);
        return services.GetRequiredService<IAccessTokenIssuer>().Issue(request);
    }

    /// <summary>Sends <paramref name="token"/> as <c>Authorization: Bearer</c>.</summary>
    internal static HttpRequestMessage WithBearer(this HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>
    /// GETs <paramref name="path"/> as a signed-in caller, with a token minted by the host behind <paramref name="services"/>. For tests of
    /// routes the fallback policy would otherwise answer with 401 (an unknown route, a branch outside the endpoints).
    /// </summary>
    internal static async Task<HttpResponseMessage> GetSignedInAsync(
        this HttpClient client,
        IServiceProvider services,
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path).WithBearer(Issue(services).Value);
        return await client.SendAsync(request, cancellationToken);
    }
}
