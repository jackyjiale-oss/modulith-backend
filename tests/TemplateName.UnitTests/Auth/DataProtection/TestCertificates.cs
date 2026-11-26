using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TemplateName.UnitTests.Auth.DataProtection;

/// <summary>Self-signed certificates generated in memory for the key ring tests; none is committed.</summary>
internal static class TestCertificates
{
    private const string TransferPassword = "test";

    /// <summary>Creates an RSA certificate; without a private key only the public part is returned.</summary>
    public static X509Certificate2 Create(string name, DateTimeOffset notBefore, DateTimeOffset notAfter, bool withPrivateKey = true)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(notBefore, notAfter);

        // Round-trip through a PFX so the instance owns its key (an ephemeral key is not usable once the RSA is disposed).
        var pfx = created.Export(X509ContentType.Pfx, TransferPassword);
        var withKey = X509CertificateLoader.LoadPkcs12(pfx, TransferPassword, X509KeyStorageFlags.Exportable);
        if (withPrivateKey)
        {
            return withKey;
        }

        using (withKey)
        {
            return X509CertificateLoader.LoadCertificate(withKey.Export(X509ContentType.Cert));
        }
    }

    /// <summary>Creates an ECDSA certificate with a private key, which the key ring cannot use.</summary>
    public static X509Certificate2 CreateEcdsa(string name, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", ecdsa, HashAlgorithmName.SHA256);
        using var created = request.CreateSelfSigned(notBefore, notAfter);
        var pfx = created.Export(X509ContentType.Pfx, TransferPassword);
        return X509CertificateLoader.LoadPkcs12(pfx, TransferPassword, X509KeyStorageFlags.Exportable);
    }

    /// <summary>Writes the certificate with its private key as a password-protected PFX and returns the path.</summary>
    public static string WritePfx(X509Certificate2 certificate, string directory, string password)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        return path;
    }
}
