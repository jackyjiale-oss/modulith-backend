namespace TemplateName.Modules.Auth.Application.ExternalLogins.Providers;

/// <summary>An enabled sign-in provider as a client shows it. Never a client ID, an app ID or a secret.</summary>
/// <param name="Name">The name to put in <c>POST /api/v1/auth/external/{provider}/login</c>, lower-case.</param>
/// <param name="DisplayName">The label for the button (<c>Google</c>, <c>Sign in with Okta</c>).</param>
/// <param name="Type">What the client posts: <c>oidc</c> (an ID token and its nonce) or <c>facebook</c> (an access token, or an iOS Limited Login ID token).</param>
internal sealed record ExternalProviderResponse(string Name, string DisplayName, string Type)
{
    /// <summary>The <see cref="Type"/> of a provider that takes an OpenID Connect ID token.</summary>
    public const string OidcType = "oidc";

    /// <summary>The <see cref="Type"/> of Facebook.</summary>
    public const string FacebookType = "facebook";

    /// <summary>Only the type name, as for every response of the module.</summary>
    public override string ToString() => nameof(ExternalProviderResponse);
}
