namespace TemplateName.Modules.Auth.Infrastructure.External;

/// <summary>
/// One entry of <c>Auth:External:Providers</c>; the key is the provider's name (<c>Google</c>, <c>Microsoft</c>, <c>Apple</c>, <c>Facebook</c>,
/// or a name of your own with <see cref="Type"/> <c>oidc</c>). Nothing is checked while <see cref="Enabled"/> is off. A message about an entry
/// names its key and never quotes a value.
/// </summary>
internal sealed class ExternalProviderOptions
{
    /// <summary>Whether the provider is offered and accepted. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>Required for a name other than the four: <c>oidc</c>. For the four it may be left out (or name its own type).</summary>
    public string? Type { get; set; }

    /// <summary>The label a front end shows. Defaults to the provider's own name (<c>Google</c>) or, for a generic entry, its key.</summary>
    public string? DisplayName { get; set; }

    /// <summary>A generic entry only: the issuer's absolute <c>https</c> URL, whose <c>/.well-known/openid-configuration</c> is read. Ignored by the four.</summary>
    public string? Authority { get; set; }

    /// <summary>The accepted <c>aud</c> values (OAuth client IDs, Apple's Services ID and bundle ID). At least one when enabled; Facebook uses <see cref="AppId"/> instead.</summary>
    public List<string> ClientIds { get; set; } = [];

    /// <summary>Microsoft only: <c>common</c> (the default), <c>organizations</c>, <c>consumers</c> or a tenant GUID, which accepts only that tenant.</summary>
    public string? TenantId { get; set; }

    /// <summary>Facebook only: the app ID, required when enabled.</summary>
    public string? AppId { get; set; }

    /// <summary>Facebook only: the app secret, required when enabled. Never in a file: user secrets or <c>Auth__External__Providers__Facebook__AppSecret</c>.</summary>
    public string? AppSecret { get; set; }

    /// <summary>Whether the provider's email may be used to find and link an account once its rule holds. Defaults on for Google, Apple and Microsoft, off for the rest; no effect for Facebook.</summary>
    public bool? TrustEmail { get; set; }
}
