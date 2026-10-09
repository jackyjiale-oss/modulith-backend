using System.Net;
using TemplateName.Modules.Auth.Application.Abstractions;

namespace TemplateName.Modules.Auth.Application.Verification;

/// <summary>
/// Builds the emails the Auth module sends. They are English only until Plan 3 adds translated templates (decision D8). The subjects
/// are fixed text, so user input never reaches a header; every user-supplied value in an HTML body is HTML-encoded, and the plain-text
/// body carries no markup.
/// </summary>
internal static class AuthEmails
{
    private const string ConfirmEmailSubject = "Confirm your email address";
    private const string ResetPasswordSubject = "Reset your password";
    private const string RegistrationAttemptedSubject = "Sign-up attempt on your account";

    /// <summary>The email that carries the confirmation <paramref name="link"/> (already built, token URL-encoded).</summary>
    public static EmailMessage ConfirmEmail(string to, string displayName, string link)
    {
        RequireLink(link);

        var text = $"""
            Hello {displayName},

            Please confirm your email address by opening this link:

            {link}

            The link works once and expires soon. If you did not sign up, you can ignore this email.
            """;

        var html = Html(
            displayName,
            $"""
            <p>Please confirm your email address.</p>
            {LinkParagraph("Confirm email address", link)}
            <p>The link works once and expires soon. If you did not sign up, you can ignore this email.</p>
            """);

        return new EmailMessage(to, ConfirmEmailSubject, text, html);
    }

    /// <summary>The email that carries the password-reset <paramref name="link"/> (already built, token URL-encoded).</summary>
    public static EmailMessage ResetPassword(string to, string displayName, string link)
    {
        RequireLink(link);

        var text = $"""
            Hello {displayName},

            We received a request to reset your password. Open this link to choose a new one:

            {link}

            The link works once and expires soon. If you did not ask for this, you can ignore this email; your password stays as it is.
            """;

        var html = Html(
            displayName,
            $"""
            <p>We received a request to reset your password.</p>
            {LinkParagraph("Choose a new password", link)}
            <p>The link works once and expires soon. If you did not ask for this, you can ignore this email; your password stays as it is.</p>
            """);

        return new EmailMessage(to, ResetPasswordSubject, text, html);
    }

    /// <summary>
    /// The email sent when someone signs up with an address that already has an account. It holds no link and no secret, and it is the
    /// only visible difference from a new sign-up, which only the address's owner can see.
    /// </summary>
    public static EmailMessage RegistrationAttempted(string to, string displayName)
    {
        var text = $"""
            Hello {displayName},

            Someone tried to create an account with your email address, but you already have one.

            If it was you, sign in with your password, or reset it if you have forgotten it. If it was not you, you can ignore this email.
            """;

        var html = Html(
            displayName,
            """
            <p>Someone tried to create an account with your email address, but you already have one.</p>
            <p>If it was you, sign in with your password, or reset it if you have forgotten it. If it was not you, you can ignore this email.</p>
            """);

        return new EmailMessage(to, RegistrationAttemptedSubject, text, html);
    }

    private static void RequireLink(string link)
    {
        if (!LinksOptions.IsAbsoluteHttp(link))
        {
            throw new ArgumentException("The link must be an absolute http or https address.", nameof(link));
        }
    }

    private static string LinkParagraph(string label, string link)
    {
        var encodedLink = WebUtility.HtmlEncode(link);
        return $"<p><a href=\"{encodedLink}\">{label}</a></p>\n<p>If the button does not work, copy this address into your browser:<br>{encodedLink}</p>";
    }

    private static string Html(string displayName, string content) => $"""
        <html>
        <body style="font-family: sans-serif; line-height: 1.5;">
        <p>Hello {WebUtility.HtmlEncode(displayName)},</p>
        {content}
        </body>
        </html>
        """;
}
