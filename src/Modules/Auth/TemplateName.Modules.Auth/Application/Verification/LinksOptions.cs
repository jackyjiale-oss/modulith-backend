using System.ComponentModel.DataAnnotations;

namespace TemplateName.Modules.Auth.Application.Verification;

/// <summary>
/// The front-end addresses the emailed links point to (section <c>Auth:Links</c>). Each URL is a template with a <c>{token}</c>
/// placeholder, which is replaced by the URL-encoded token. The event handlers that send the emails use it.
/// </summary>
internal sealed class LinksOptions : IValidatableObject
{
    internal const string SectionName = "Auth:Links";

    private const string TokenPlaceholder = "{token}";

    /// <summary>Where the email-confirmation link leads.</summary>
    public string ConfirmEmailUrl { get; set; } = "http://localhost:3000/confirm-email?token={token}";

    /// <summary>Where the password-reset link leads.</summary>
    public string ResetPasswordUrl { get; set; } = "http://localhost:3000/reset-password?token={token}";

    /// <summary>The confirmation link for <paramref name="token"/>, URL-encoded.</summary>
    public string ConfirmEmailLink(string token) => Substitute(ConfirmEmailUrl, token);

    /// <summary>The password-reset link for <paramref name="token"/>, URL-encoded.</summary>
    public string ResetPasswordLink(string token) => Substitute(ResetPasswordUrl, token);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var result in ValidateTemplate(ConfirmEmailUrl, nameof(ConfirmEmailUrl)))
        {
            yield return result;
        }

        foreach (var result in ValidateTemplate(ResetPasswordUrl, nameof(ResetPasswordUrl)))
        {
            yield return result;
        }
    }

    private static string Substitute(string template, string token)
        => template.Replace(TokenPlaceholder, Uri.EscapeDataString(token), StringComparison.Ordinal);

    private static IEnumerable<ValidationResult> ValidateTemplate(string url, string propertyName)
    {
        if (!url.Contains(TokenPlaceholder, StringComparison.Ordinal))
        {
            yield return new ValidationResult($"{propertyName} must contain the {TokenPlaceholder} placeholder.", [propertyName]);
        }

        if (!IsAbsoluteHttp(url.Replace(TokenPlaceholder, "token", StringComparison.Ordinal)))
        {
            yield return new ValidationResult($"{propertyName} must be an absolute http or https address.", [propertyName]);
        }
    }

    /// <summary>Whether <paramref name="url"/> is an absolute http or https address.</summary>
    internal static bool IsAbsoluteHttp(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
