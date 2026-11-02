namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>How a provider's proof is verified: an OpenID Connect ID token, or Facebook's Graph API (access token) plus its Limited Login ID token.</summary>
internal enum ExternalProviderKind
{
    /// <summary>The generic OpenID Connect verifier: Google, Apple, Microsoft and any <c>Type: "oidc"</c> entry.</summary>
    Oidc,

    /// <summary>Facebook: <c>debug_token</c> and <c>/me</c> on the Graph API for an access token, the OIDC verifier for an iOS Limited Login ID token.</summary>
    Facebook,
}

/// <summary>Which claim of a verified token makes its email usable for finding or linking an account (before the provider's <c>TrustEmail</c> is applied).</summary>
internal enum ExternalEmailRule
{
    /// <summary>The token's <c>email_verified</c> is true (a boolean or the string <c>"true"</c>): Apple and a generic OIDC provider.</summary>
    EmailVerifiedClaim,

    /// <summary>
    /// Google's: <c>email_verified</c> is true (as for <see cref="EmailVerifiedClaim"/>) <b>and</b> the address ends in <c>@gmail.com</c>
    /// (without regard to case) or the token carries a non-blank string <c>hd</c> (a Workspace account). Google documents
    /// <c>email_verified</c> as authoritative only for those; for any other address the verification may be stale (Ruling R15).
    /// </summary>
    GoogleVerifiedGmailOrWorkspace,

    /// <summary>The token's <c>xms_edov</c> is true, the only Microsoft claim that says the email's domain was verified; its <c>email</c> never is.</summary>
    MicrosoftDomainVerified,

    /// <summary>The email is never usable (Facebook).</summary>
    Never,
}

/// <summary>
/// One enabled provider as the verifier and the handlers see it, built from the preset and the configuration. <paramref name="Name"/> is
/// trimmed lower-case and is the value stored as <c>Provider</c>. <paramref name="Authority"/> is the base of the discovery document (its
/// <c>/.well-known/openid-configuration</c>; Microsoft's has the configured tenant in it, <c>common</c> by default), <paramref name="Issuers"/>
/// the accepted <c>iss</c> values (Microsoft's holds the literal <c>{tenantid}</c> that stands for the token's own <c>tid</c>),
/// <paramref name="ClientIds"/> the accepted <c>aud</c> values (Facebook: its <paramref name="AppId"/>), <paramref name="TenantId"/>
/// Microsoft's tenant (<c>common</c>, <c>organizations</c>, <c>consumers</c> or a lower-case GUID; only a GUID restricts sign-in to that
/// tenant) and null for the others. <paramref name="AppId"/> and <paramref name="AppSecret"/> are Facebook's. <paramref name="MetadataAddress"/>
/// is the exact address of the discovery document when it is not <c>{Authority}/.well-known/openid-configuration</c> (Facebook's ends with
/// a slash and must be fetched as written), else null. The record prints only its name (<see cref="ToString"/>), so the secret never reaches a log.
/// </summary>
internal sealed record ExternalProvider(
    string Name,
    string DisplayName,
    ExternalProviderKind Kind,
    string? Authority,
    IReadOnlyList<string> Issuers,
    IReadOnlyList<string> ClientIds,
    ExternalEmailRule EmailRule,
    bool TrustEmail,
    string? TenantId,
    string? AppId,
    string? AppSecret,
    string? MetadataAddress = null)
{
    /// <summary>The provider's name only: the generated <c>ToString</c> would print <see cref="AppSecret"/>.</summary>
    public override string ToString() => Name;
}
