using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Modules.Auth.Infrastructure.DataProtection;

namespace TemplateName.UnitTests.Auth.DataProtection;

public sealed class KeyRingProtectionTests : IDisposable
{
    private const string ApplicationName = "KeyRingTests";
    private const string Purpose = "test-purpose";
    private static readonly DateTimeOffset NotBefore = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NotAfter = new(2036, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyring-prot-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void A_second_provider_reads_a_protected_ring()
    {
        using var certificate = TestCertificates.Create("active", NotBefore, NotAfter);
        using var first = Build(certificate, []);
        var payload = first.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("hello");

        using var second = Build(certificate, []);

        second.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(payload).ShouldBe("hello");
    }

    [Fact]
    public void The_key_file_holds_an_encryptedSecret_and_no_masterKey()
    {
        using var certificate = TestCertificates.Create("active", NotBefore, NotAfter);
        using var provider = Build(certificate, []);
        provider.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("hello");

        var xml = string.Concat(Directory.GetFiles(_directory, "key-*.xml").Select(File.ReadAllText));

        xml.ShouldContain("encryptedSecret");
        xml.ShouldNotContain("masterKey");
    }

    [Fact]
    public void After_rotation_old_keys_stay_readable()
    {
        using var certificateA = TestCertificates.Create("a", NotBefore, NotAfter);
        using var certificateB = TestCertificates.Create("b", NotBefore, NotAfter);
        using var first = Build(certificateA, []);
        var payloadA = first.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("under A");

        // The rotation: B is active, A is kept as a previous certificate; a new key is created under B.
        using var rotated = Build(certificateB, [certificateA]);
        var now = DateTimeOffset.UtcNow;
        rotated.GetRequiredService<IKeyManager>().CreateNewKey(now, now.AddDays(90));
        var protector = rotated.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
        var payloadB = protector.Protect("under B");

        protector.Unprotect(payloadA).ShouldBe("under A");
        protector.Unprotect(payloadB).ShouldBe("under B");
    }

    [Fact]
    public void Without_the_old_certificate_old_keys_are_not_readable()
    {
        using var certificateA = TestCertificates.Create("a", NotBefore, NotAfter);
        using var certificateB = TestCertificates.Create("b", NotBefore, NotAfter);
        using var first = Build(certificateA, []);
        var payloadA = first.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("under A");

        using var withoutA = Build(certificateB, []);
        var protector = withoutA.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

        Should.Throw<CryptographicException>(() => protector.Unprotect(payloadA));
    }

    private ServiceProvider Build(X509Certificate2 active, IReadOnlyList<X509Certificate2> previous)
    {
        var services = new ServiceCollection();
        var builder = services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(_directory));
        KeyRingCertificates.Protect(builder, new KeyRingCertificateSet(active, previous, []));
        return services.BuildServiceProvider();
    }
}
