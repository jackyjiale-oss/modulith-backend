using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemplateName.Modules.Auth.Infrastructure.Tokens;

namespace TemplateName.Modules.Auth.Infrastructure.DataProtection;

/// <summary>
/// The certificates loaded for the key ring. <see cref="Problems"/> names each setting that could not be used; a problem never holds
/// the password or any key material. Validity dates are not checked here (the startup check does, with the clock).
/// </summary>
internal sealed record KeyRingCertificateSet(
    X509Certificate2? Active,
    IReadOnlyList<X509Certificate2> Previous,
    IReadOnlyList<string> Problems)
{
    /// <summary>Whether an active certificate was loaded, so the ring is wrapped.</summary>
    public bool Configured => Active is not null;
}

/// <summary>Loads the key ring certificates from the settings and applies them to the Data Protection builder.</summary>
internal static class KeyRingCertificates
{
    private const string CertificateSetting = KeyRingCertificateOptions.SectionName + ":Certificate";
    private const string PreviousSetting = KeyRingCertificateOptions.SectionName + ":PreviousCertificates";

    /// <summary>
    /// Reads section <c>Auth:DataProtection</c> and loads it; a value that cannot be bound (an unknown <c>Source</c>, say) becomes a
    /// problem instead of an exception, so registration never throws. No section is an unconfigured set.
    /// </summary>
    public static KeyRingCertificateSet Load(IConfiguration configuration)
    {
        try
        {
            var options = configuration.GetSection(KeyRingCertificateOptions.SectionName).Get<KeyRingCertificateOptions>();
            return Load(options ?? new KeyRingCertificateOptions());
        }
        catch (InvalidOperationException)
        {
            return new KeyRingCertificateSet(
                null,
                [],
                [$"{KeyRingCertificateOptions.SectionName} could not be read: a value is not valid (Source must be Store or File, StoreLocation and StoreName must name a store)."]);
        }
    }

    /// <summary>
    /// Loads the active and previous certificates. It never throws for a bad setting: each failure becomes a problem naming the setting,
    /// and the certificate is left out.
    /// </summary>
    public static KeyRingCertificateSet Load(KeyRingCertificateOptions options)
    {
        var problems = new List<string>();
        var active = options.Certificate is null ? null : LoadOne(options.Certificate, CertificateSetting, problems);

        var previous = new List<X509Certificate2>();
        for (var index = 0; index < options.PreviousCertificates.Count; index++)
        {
            var certificate = LoadOne(options.PreviousCertificates[index], $"{PreviousSetting}:{index}", problems);
            if (certificate is not null)
            {
                previous.Add(certificate);
            }
        }

        if (options.Certificate is null && options.PreviousCertificates.Count > 0)
        {
            problems.Add($"{PreviousSetting} is set but {CertificateSetting} is not; set the active certificate.");
        }

        return new KeyRingCertificateSet(active, previous, problems);
    }

    /// <summary>
    /// Wraps new keys with the active certificate and unwraps with the active and previous ones. Does nothing when no certificate is
    /// active. The framework's thumbprint overload reads the store lazily, so the loaded certificate is passed instead.
    /// </summary>
    public static void Protect(IDataProtectionBuilder builder, KeyRingCertificateSet set)
    {
        if (set.Active is null)
        {
            return;
        }

        builder.ProtectKeysWithCertificate(set.Active);
        builder.UnprotectKeysWithAnyCertificate([set.Active, .. set.Previous]);
    }

    /// <summary>
    /// Outside Development and Testing, switches automatic key generation off while no certificate is active, so the framework never
    /// stores a key unwrapped. Its own start-up step (<c>DataProtectionHostedService</c>) makes a key when the ring has none, and it runs
    /// before <c>KeyRingStartupCheck</c>; a plaintext key saved to the shared table then would be adopted by other instances and would
    /// make every restart after the configuration is fixed refuse. Without a key the framework logs the failure and the check refuses
    /// the host with its own message. The environment is read when the options are built, not at registration.
    /// </summary>
    public static void RequireCertificateToCreateKeys(IDataProtectionBuilder builder, KeyRingCertificateSet set)
    {
        if (set.Active is not null)
        {
            return;
        }

        builder.Services.AddOptions<KeyManagementOptions>().Configure<IHostEnvironment>((options, environment) =>
        {
            if (!SigningKeyProvider.AllowsEphemeralKey(environment))
            {
                options.AutoGenerateKeys = false;
            }
        });
    }

    private static X509Certificate2? LoadOne(CertificateReference settings, string setting, List<string> problems)
    {
        var certificate = settings.Source == CertificateSource.File
            ? LoadFile(settings, setting, problems)
            : LoadFromStore(settings, setting, problems);
        if (certificate is null)
        {
            return null;
        }

        if (!certificate.HasPrivateKey)
        {
            problems.Add($"{setting}: the certificate has no private key, so it cannot unwrap the key ring.");
            certificate.Dispose();
            return null;
        }

        if (!HasUsableRsaKey(certificate))
        {
            problems.Add($"{setting}: the certificate is not an RSA certificate with an accessible private key; the key ring needs RSA.");
            certificate.Dispose();
            return null;
        }

        return certificate;
    }

    private static X509Certificate2? LoadFile(CertificateReference settings, string setting, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(settings.Path))
        {
            problems.Add($"{setting}:Path is required when Source is File.");
            return null;
        }

        if (!File.Exists(settings.Path))
        {
            problems.Add($"{setting}:Path points to a file that does not exist.");
            return null;
        }

        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(settings.Path, settings.Password, PfxFlags());
        }
        catch (CryptographicException)
        {
            // A plain certificate file loads without a key; tell that apart from a wrong password.
            try
            {
                using var withoutKey = X509CertificateLoader.LoadCertificateFromFile(settings.Path);
                problems.Add($"{setting}:Path holds a certificate with no private key; supply a PFX that includes it.");
            }
            catch (CryptographicException)
            {
                problems.Add($"{setting}:Password does not open the PFX file at {setting}:Path, or the file is not a PFX.");
            }

            return null;
        }
        catch (IOException)
        {
            problems.Add($"{setting}:Path points to a file that cannot be read.");
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            problems.Add($"{setting}:Path points to a file the application is not allowed to read.");
            return null;
        }
    }

    private static X509Certificate2? LoadFromStore(CertificateReference settings, string setting, List<string> problems)
    {
        var thumbprint = Normalize(settings.Thumbprint);
        if (thumbprint.Length == 0)
        {
            problems.Add($"{setting}:Thumbprint is required when Source is Store (to read a file, set {setting}:Source to File).");
            return null;
        }

        try
        {
            using var store = new X509Store(settings.StoreName, settings.StoreLocation);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            if (found.Count == 0)
            {
                problems.Add($"{setting}:Thumbprint was not found in the {settings.StoreLocation}/{settings.StoreName} certificate store.");
                return null;
            }

            return found[0];
        }
        catch (CryptographicException)
        {
            problems.Add($"{setting}:Thumbprint could not be looked up: the {settings.StoreLocation}/{settings.StoreName} store cannot be opened.");
            return null;
        }
    }

    private static bool HasUsableRsaKey(X509Certificate2 certificate)
    {
        try
        {
            using var rsa = certificate.GetRSAPrivateKey();
            return rsa is not null;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    // The key stays out of the user's profile where the platform allows it (macOS has no ephemeral PFX import).
    private static X509KeyStorageFlags PfxFlags() =>
        OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;

    // A thumbprint copied from the certificate dialog carries spaces and an invisible mark; keep only the hex digits.
    private static string Normalize(string? thumbprint) =>
        thumbprint is null ? string.Empty : new string(thumbprint.Where(Uri.IsHexDigit).ToArray());
}
