using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Modules.Auth.Infrastructure.DataProtection;

namespace TemplateName.UnitTests.Auth.DataProtection;

/// <summary>
/// Key ring documents produced by the framework itself, in a temporary directory that is removed before the call returns, so the tests
/// of the ring inspection and of the startup check run against real output and not a hand-written sample.
/// </summary>
internal static class KeyRingFixtures
{
    /// <summary>
    /// Creates <paramref name="keys"/> keys in a ring wrapped with <paramref name="certificate"/> (or not wrapped when it is
    /// <see langword="null"/>), optionally revokes them all, and returns every document of the ring (<c>key</c> and <c>revocation</c>).
    /// </summary>
    public static IReadOnlyList<string> CreateRing(X509Certificate2? certificate, int keys = 1, bool revoke = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "keyring-fixture-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            var builder = services.AddDataProtection()
                .SetApplicationName("KeyRingFixture")
                .PersistKeysToFileSystem(new DirectoryInfo(directory));
            KeyRingCertificates.Protect(builder, new KeyRingCertificateSet(certificate, [], []));

            using var provider = services.BuildServiceProvider();
            var manager = provider.GetRequiredService<IKeyManager>();

            // The framework's key manager reads the real clock, so the fixture does too.
            var now = DateTimeOffset.UtcNow;
            for (var index = 0; index < keys; index++)
            {
                manager.CreateNewKey(now, now.AddDays(90));
            }

            if (revoke)
            {
                manager.RevokeAllKeys(now, "test");
            }

            return Directory.GetFiles(directory, "*.xml").Order(StringComparer.Ordinal).Select(File.ReadAllText).ToList();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
