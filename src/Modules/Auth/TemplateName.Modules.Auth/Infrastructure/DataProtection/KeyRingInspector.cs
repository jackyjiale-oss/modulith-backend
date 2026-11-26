using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace TemplateName.Modules.Auth.Infrastructure.DataProtection;

/// <summary>What the key ring holds: how many keys are stored unwrapped, and the certificates the others are wrapped under.</summary>
/// <param name="PlaintextKeys">The number of <c>key</c> documents with no <c>encryptedSecret</c>, whose secret is readable by anyone with the database.</param>
/// <param name="WrappingThumbprints">The SHA-1 thumbprints (upper-case hex) of the certificates embedded in the wrapped keys.</param>
internal readonly record struct KeyRingInspection(int PlaintextKeys, IReadOnlySet<string> WrappingThumbprints);

/// <summary>
/// Reads the XML documents of the Data Protection key ring (the <c>Xml</c> column of <c>auth.DataProtectionKeys</c>) and reports whether
/// each key is wrapped. The framework writes a key's secret as a <c>masterKey</c> element when nothing wraps it and as an
/// <c>encryptedSecret</c> element, which embeds the wrapping certificate as <c>X509Certificate</c>, when a certificate does. Pure: no
/// database and no clock.
/// </summary>
internal static class KeyRingInspector
{
    private const string KeyElement = "key";
    private const string EncryptedSecretElement = "encryptedSecret";
    private const string CertificateElement = "X509Certificate";

    /// <summary>
    /// Counts the plaintext keys and collects the wrapping thumbprints. A document whose root is not <c>key</c> (a revocation, say) is
    /// ignored, and so is one that is not XML (the framework skips it too when it loads the ring).
    /// </summary>
    public static KeyRingInspection Inspect(IEnumerable<string> documents)
    {
        var plaintext = 0;
        var thumbprints = new HashSet<string>(StringComparer.Ordinal);

        foreach (var text in documents)
        {
            var root = Parse(text);
            if (root is null || root.Name.LocalName != KeyElement)
            {
                continue;
            }

            var secret = root.Descendants().FirstOrDefault(element => element.Name.LocalName == EncryptedSecretElement);
            if (secret is null)
            {
                plaintext++;
                continue;
            }

            foreach (var certificate in secret.Descendants().Where(element => element.Name.LocalName == CertificateElement))
            {
                if (TryThumbprint(certificate.Value, out var thumbprint))
                {
                    thumbprints.Add(thumbprint);
                }
            }
        }

        return new KeyRingInspection(plaintext, thumbprints);
    }

    private static XElement? Parse(string text)
    {
        try
        {
            // No DTD: the column is data from the database, parsed as untrusted.
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader).Root;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static bool TryThumbprint(string base64, out string thumbprint)
    {
        thumbprint = string.Empty;
        var raw = new byte[base64.Length];
        if (!Convert.TryFromBase64String(base64.Trim(), raw, out var written))
        {
            return false;
        }

        // The thumbprint Windows and X509Certificate2.Thumbprint show is the SHA-1 of the DER bytes.
        thumbprint = Convert.ToHexString(SHA1.HashData(raw.AsSpan(0, written)));
        return true;
    }
}
