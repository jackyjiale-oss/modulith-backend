using System.Diagnostics.CodeAnalysis;
using TemplateName.Modules.Auth.Application.Abstractions;

namespace TemplateName.Modules.Auth.Infrastructure.External;

/// <summary>
/// What is fixed about the four named providers: the issuers and the email rule, each read from the provider's live discovery document on
/// 2026-10-30 (all four sign with <c>RS256</c>). A name that is not one of them is a generic OpenID Connect entry.
/// </summary>
internal static class ExternalProviderPresets
{
    internal const string Google = "google";
    internal const string Apple = "apple";
    internal const string Microsoft = "microsoft";
    internal const string Facebook = "facebook";

    /// <summary>The <c>Type</c> of a generic entry, and of Google, Apple and Microsoft.</summary>
    internal const string OidcType = "oidc";

    /// <summary>The <c>Type</c> of Facebook.</summary>
    internal const string FacebookType = "facebook";

    /// <summary>Stands for the token's own <c>tid</c> in Microsoft's issuer, which differs per tenant.</summary>
    internal const string TenantPlaceholder = "{tenantid}";

    /// <summary>Microsoft's tenant when none is configured: work, school and personal accounts alike.</summary>
    internal const string DefaultMicrosoftTenant = "common";

    private const string MicrosoftBase = "https://login.microsoftonline.com/";
    private const string MicrosoftSuffix = "/v2.0";

    private static readonly Dictionary<string, Preset> Presets = new(StringComparer.Ordinal)
    {
        [Google] = new Preset(
            "Google",
            ExternalProviderKind.Oidc,
            "https://accounts.google.com",
            ["https://accounts.google.com", "accounts.google.com"],
            ExternalEmailRule.EmailVerifiedClaim,
            TrustEmail: true),
        [Apple] = new Preset(
            "Apple",
            ExternalProviderKind.Oidc,
            "https://appleid.apple.com",
            ["https://appleid.apple.com"],
            ExternalEmailRule.EmailVerifiedClaim,
            TrustEmail: true),
        [Microsoft] = new Preset(
            "Microsoft",
            ExternalProviderKind.Oidc,
            MicrosoftBase + TenantPlaceholder + MicrosoftSuffix,
            [MicrosoftBase + TenantPlaceholder + MicrosoftSuffix],
            ExternalEmailRule.MicrosoftDomainVerified,
            TrustEmail: true),
        [Facebook] = new Preset(
            "Facebook",
            ExternalProviderKind.Facebook,
            "https://www.facebook.com",
            ["https://www.facebook.com"],
            ExternalEmailRule.Never,
            TrustEmail: false),
    };

    /// <summary>The form every provider name is stored and compared in: trimmed and lower-case.</summary>
    internal static string NormalizeName(string name) => name.Trim().ToLowerInvariant();

    /// <summary>The preset of a normalised name, false for a generic entry.</summary>
    internal static bool TryGet(string normalizedName, [NotNullWhen(true)] out Preset? preset) =>
        Presets.TryGetValue(normalizedName, out preset);

    /// <summary>The <c>Type</c> an entry of this kind may state (and the <c>type</c> the providers list shows).</summary>
    internal static string TypeNameOf(ExternalProviderKind kind) => kind == ExternalProviderKind.Facebook ? FacebookType : OidcType;

    /// <summary>
    /// Reads a Microsoft <c>TenantId</c>: blank is <c>common</c>; <c>common</c>, <c>organizations</c> and <c>consumers</c> are lower-cased; a GUID
    /// is written lower-case in the <c>D</c> format (the form of a token's <c>tid</c>). Anything else (a domain name, say) is refused.
    /// </summary>
    internal static bool TryNormalizeTenant(string? value, out string tenantId)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            tenantId = DefaultMicrosoftTenant;
            return true;
        }

        var lower = trimmed.ToLowerInvariant();
        if (lower is DefaultMicrosoftTenant or "organizations" or "consumers")
        {
            tenantId = lower;
            return true;
        }

        if (Guid.TryParse(trimmed, out var guid))
        {
            tenantId = guid.ToString("D");
            return true;
        }

        tenantId = DefaultMicrosoftTenant;
        return false;
    }

    /// <summary>The discovery base of Microsoft for a tenant (<c>common</c>, <c>organizations</c>, <c>consumers</c> or a GUID).</summary>
    internal static string MicrosoftAuthority(string tenantId) => MicrosoftBase + tenantId + MicrosoftSuffix;

    /// <summary>
    /// The fixed part of a named provider. <see cref="Authority"/> is the discovery base (Microsoft's keeps <see cref="TenantPlaceholder"/>
    /// until a tenant is chosen) and <see cref="TrustEmail"/> the default of the provider's <c>TrustEmail</c>.
    /// </summary>
    internal sealed record Preset(
        string DisplayName,
        ExternalProviderKind Kind,
        string Authority,
        IReadOnlyList<string> Issuers,
        ExternalEmailRule EmailRule,
        bool TrustEmail);
}
