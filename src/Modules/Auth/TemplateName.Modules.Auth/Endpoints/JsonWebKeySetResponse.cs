using Microsoft.IdentityModel.Tokens;

namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>The body of <c>GET /.well-known/jwks.json</c> (RFC 7517): only the public members of each EC key, so no private part can be written.</summary>
internal sealed record JsonWebKeySetResponse(IReadOnlyList<JsonWebKeySetResponse.Key> Keys)
{
    internal static JsonWebKeySetResponse From(JsonWebKeySet keySet) =>
        new(keySet.Keys.Select(key => new Key(key.Kty, key.Crv, key.X, key.Y, key.Kid, key.Alg, key.Use)).ToList());

    /// <summary>One public EC key: <c>kty</c>, <c>crv</c>, <c>x</c>, <c>y</c>, <c>kid</c>, <c>alg</c> and <c>use</c>.</summary>
    internal sealed record Key(string Kty, string Crv, string X, string Y, string Kid, string Alg, string Use);
}
