using System.Security.Cryptography.X509Certificates;

namespace TemplateName.Modules.Auth.Infrastructure.DataProtection;

/// <summary>Where a key ring certificate comes from.</summary>
internal enum CertificateSource
{
    /// <summary>A certificate with a private key in a Windows or Linux certificate store, found by thumbprint.</summary>
    Store,

    /// <summary>A password-protected PFX file.</summary>
    File,
}

/// <summary>One certificate the key ring is wrapped with: its source and the fields that source needs.</summary>
internal sealed class CertificateReference
{
    /// <summary>Where the certificate comes from.</summary>
    public CertificateSource Source { get; set; }

    /// <summary>The thumbprint to look up (<see cref="CertificateSource.Store"/>).</summary>
    public string? Thumbprint { get; set; }

    /// <summary>The store's location (<see cref="CertificateSource.Store"/>).</summary>
    public StoreLocation StoreLocation { get; set; } = StoreLocation.LocalMachine;

    /// <summary>The store's name (<see cref="CertificateSource.Store"/>).</summary>
    public StoreName StoreName { get; set; } = StoreName.My;

    /// <summary>The PFX file's path (<see cref="CertificateSource.File"/>).</summary>
    public string? Path { get; set; }

    /// <summary>The PFX file's password (<see cref="CertificateSource.File"/>); supply it from a secret store, never a committed file.</summary>
    public string? Password { get; set; }
}

/// <summary>
/// The certificates that wrap the Data Protection key ring (section <c>Auth:DataProtection</c>): the active one that wraps every
/// new key, and the previous ones that can still unwrap keys written before a rotation.
/// </summary>
internal sealed class KeyRingCertificateOptions
{
    internal const string SectionName = "Auth:DataProtection";

    /// <summary>The certificate that wraps the key ring; <see langword="null"/> leaves the ring unwrapped (development only).</summary>
    public CertificateReference? Certificate { get; set; }

    /// <summary>Certificates that wrapped the ring before a rotation; they only unwrap.</summary>
    public List<CertificateReference> PreviousCertificates { get; set; } = [];
}
