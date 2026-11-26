using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Infrastructure.DataProtection;

namespace TemplateName.UnitTests.Auth.DataProtection;

/// <summary>
/// Outside Development and Testing the framework must never make a key while no certificate is active: that key would be stored
/// unwrapped in the shared table, before the startup check refuses the host.
/// </summary>
public sealed class KeyRingKeyCreationTests : IDisposable
{
    private static readonly DateTimeOffset NotBefore = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NotAfter = new(2036, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyring-creation-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    [InlineData("Development", true)]
    [InlineData("Testing", true)]
    public void Without_a_certificate_automatic_key_generation_is_on_only_where_a_plaintext_ring_is_allowed(string environment, bool expected)
    {
        using var provider = Build(environment, active: null);

        provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.AutoGenerateKeys.ShouldBe(expected);
    }

    [Fact]
    public void With_a_certificate_automatic_key_generation_stays_on_in_production()
    {
        using var certificate = TestCertificates.Create("active", NotBefore, NotAfter);
        using var provider = Build("Production", certificate);

        provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.AutoGenerateKeys.ShouldBeTrue();
    }

    [Fact]
    public void In_production_without_a_certificate_protecting_a_value_makes_no_key()
    {
        using var provider = Build("Production", active: null);

        Should.Throw<CryptographicException>(() => provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("p").Protect("secret"));

        Directory.GetFiles(_directory, "*.xml").ShouldBeEmpty();
    }

    [Fact]
    public void In_development_without_a_certificate_protecting_a_value_makes_a_key()
    {
        using var provider = Build("Development", active: null);

        provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("p").Protect("secret");

        Directory.GetFiles(_directory, "key-*.xml").ShouldNotBeEmpty();
    }

    private ServiceProvider Build(string environment, X509Certificate2? active)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TestSigningKeys.Environment(environment));
        var builder = services.AddDataProtection()
            .SetApplicationName("KeyCreationTests")
            .PersistKeysToFileSystem(new DirectoryInfo(_directory));
        var set = new KeyRingCertificateSet(active, [], []);
        KeyRingCertificates.Protect(builder, set);
        KeyRingCertificates.RequireCertificateToCreateKeys(builder, set);
        return services.BuildServiceProvider();
    }
}
