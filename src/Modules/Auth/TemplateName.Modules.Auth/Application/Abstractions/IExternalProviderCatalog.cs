namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>The configured social-login providers (<c>Auth:External:Providers</c>) and the sign-up switch, read once at start-up.</summary>
internal interface IExternalProviderCatalog
{
    /// <summary>
    /// <c>Auth:External:AllowSignUp</c>: whether a first social sign-in with no matching account creates one. Here, not in the options the
    /// handlers cannot see (the options class is the Infrastructure layer's).
    /// </summary>
    bool AllowSignUp { get; }

    /// <summary>The enabled providers, in name order. A disabled provider is not listed.</summary>
    IReadOnlyList<ExternalProvider> Enabled { get; }

    /// <summary>The enabled provider with this name (case-insensitive, surrounding spaces ignored), or null when it is unknown, blank or disabled.</summary>
    ExternalProvider? Find(string name);

    /// <summary>The display name of a configured provider, enabled or not (an unlink screen names a provider that was switched off); the name itself when none is configured.</summary>
    string DisplayNameOf(string name);
}
