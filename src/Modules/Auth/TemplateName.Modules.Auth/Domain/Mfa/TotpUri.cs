using System.Globalization;

namespace TemplateName.Modules.Auth.Domain.Mfa;

/// <summary>
/// The <c>otpauth://totp/</c> URI (Google Authenticator "Key Uri Format") that a client renders as a QR code for an authenticator
/// app. The server builds the URI only; drawing the QR code is the client's job.
/// </summary>
internal static class TotpUri
{
    /// <summary>
    /// <c>otpauth://totp/{issuer}:{accountName}?secret={BASE32}&amp;issuer={issuer}&amp;algorithm=SHA1&amp;digits=6&amp;period=30</c>,
    /// with <paramref name="issuer"/> and <paramref name="accountName"/> escaped by <see cref="Uri.EscapeDataString(string)"/> (so a
    /// colon in either cannot add a label separator) and the secret in unpadded <see cref="Base32"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="issuer"/> or <paramref name="accountName"/> is blank, or <paramref name="secret"/> is empty.</exception>
    public static string Build(string issuer, string accountName, ReadOnlySpan<byte> secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        if (secret.IsEmpty)
        {
            throw new ArgumentException("The secret is empty.", nameof(secret));
        }

        var escapedIssuer = Uri.EscapeDataString(issuer);
        var escapedAccountName = Uri.EscapeDataString(accountName);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"otpauth://totp/{escapedIssuer}:{escapedAccountName}?secret={Base32.Encode(secret)}&issuer={escapedIssuer}"
            + $"&algorithm=SHA1&digits={Totp.Digits}&period={Totp.StepSeconds}");
    }
}
