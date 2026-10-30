namespace TemplateName.Modules.Auth.Infrastructure.External;

/// <summary>
/// The names of the named <see cref="HttpClient"/>s of social login. Each is also the name of its resilience pipeline
/// (<c>Resilience:Pipelines:{name}</c>), so a deployment can tune one without touching the other.
/// </summary>
internal static class ExternalHttpClients
{
    /// <summary>Fetches an OpenID Connect provider's discovery document and signing keys.</summary>
    internal const string OidcDiscovery = "oidc-discovery";

    /// <summary>Calls Facebook's Graph API (<c>debug_token</c> and <c>/me</c>).</summary>
    internal const string FacebookGraph = "facebook-graph";
}
