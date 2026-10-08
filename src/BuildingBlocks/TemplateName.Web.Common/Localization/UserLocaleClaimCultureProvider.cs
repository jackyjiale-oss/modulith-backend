using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Primitives;

namespace TemplateName.Web.Common.Localization;

/// <summary>
/// The signed-in user's saved language: the <c>locale</c> claim of the authenticated request principal, offered as the UI culture only
/// (the formatting culture stays the default). It runs before <see cref="AcceptLanguageHeaderRequestCultureProvider"/>, so the saved
/// language wins over the browser's (decision D7). A missing claim, or one the API does not support (after parent fallback), leaves the
/// choice to the next provider. Needs <c>UseAuthentication</c> ahead of <c>UseApiLocalization</c>.
/// </summary>
public sealed class UserLocaleClaimCultureProvider : RequestCultureProvider
{
    /// <summary>The claim holding the user's saved language, a culture name such as <c>ms</c>.</summary>
    public const string LocaleClaimType = "locale";

    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var user = httpContext.User;
        var locale = user.Identity?.IsAuthenticated == true ? user.FindFirst(LocaleClaimType)?.Value : null;
        if (string.IsNullOrWhiteSpace(locale))
        {
            return NullProviderCultureResult;
        }

        // No formatting culture: the middleware keeps the default one. When the UI culture is unsupported too, the middleware finds
        // nothing in this result and asks the next provider instead of falling back to the default.
        return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult([], [new StringSegment(locale.Trim())]));
    }
}
