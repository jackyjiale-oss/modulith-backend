using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using TemplateName.Modules.Auth.Infrastructure.DataProtection;

namespace TemplateName.UnitTests.Auth.DataProtection;

public sealed class KeyRingCertificatesTests : IDisposable
{
    private const string Password = "s3cret-pfx-password";
    private static readonly DateTimeOffset NotBefore = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NotAfter = new(2036, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyring-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Loads_a_file_certificate()
    {
        using var certificate = TestCertificates.Create("active", NotBefore, NotAfter);

        var set = KeyRingCertificates.Load(new KeyRingCertificateOptions { Certificate = FileSettings(certificate) });

        set.Problems.ShouldBeEmpty();
        set.Configured.ShouldBeTrue();
        set.Active.ShouldNotBeNull();
        set.Active.Thumbprint.ShouldBe(certificate.Thumbprint);
        set.Active.HasPrivateKey.ShouldBeTrue();
        set.Previous.ShouldBeEmpty();
    }

    [Fact]
    public void Loads_previous_certificates()
    {
        using var active = TestCertificates.Create("active", NotBefore, NotAfter);
        using var old = TestCertificates.Create("old", NotBefore, NotAfter);

        var set = KeyRingCertificates.Load(new KeyRingCertificateOptions
        {
            Certificate = FileSettings(active),
            PreviousCertificates = [FileSettings(old)],
        });

        set.Problems.ShouldBeEmpty();
        set.Previous.Count.ShouldBe(1);
        set.Previous[0].Thumbprint.ShouldBe(old.Thumbprint);
    }

    [Fact]
    public void A_missing_file_is_a_problem_naming_the_path_setting()
    {
        var set = KeyRingCertificates.Load(new KeyRingCertificateOptions
        {
            Certificate = new CertificateReference { Source = CertificateSource.File, Path = Path.Combine(_directory, "missing.pfx"), Password = Password },
        });

        set.Active.ShouldBeNull();
        set.Problems.ShouldContain(problem => problem.Contains("Auth:DataProtection:Certificate:Path"));
    }

    [Fact]
    public void A_wrong_password_is_a_problem_that_does_not_contain_the_password()
    {
        using var certificate = TestCertificates.Create("active", NotBefore, NotAfter);
        var settings = FileSettings(certificate);
        settings.Password = "wrong-password-value";

        var set = KeyRingCertificates.Load(new KeyRingCertificateOptions { Certificate = settings });

        set.Active.ShouldBeNull();
        set.Problems.ShouldNotBeEmpty();
        set.Problems.ShouldContain(problem => problem.Contains("Auth:DataProtection:Certificate:Password"));
        set.Problems.ShouldAllBe(problem => !problem.Contains("wrong-password-value") && !problem.Contains(Password));
    }

    [Fact]
    public void A_certificate_without_a_private_key_is_a_problem()
    {
        using var certificate = TestCertificates.Create("public-only", NotBefore, NotAfter, withPrivateKey: false);
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "public.cer");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Cert));

        var set = KeyRingCertificates.Load(new KeyRingCertificateOptions
        {
            Certificate = new CertificateReference { Source = CertificateSource.File, Path = path, Password = Password },
        });

        set.Active.ShouldBeNull();
        set.Problems.ShouldContain(problem => problem.Contains("Auth:DataProtection:Certificate") && problem.Contains("private key"));
    }

    [Fact]
    public void A_non_RSA_certificate_is_a_problem()
    {
        using var certificate = TestCertificates.CreateEcdsa("ecdsa", NotBefore, NotAfter);

        var set = KeyRingCertificates.Load(new KeyRingCertificateOptions { Certificate = FileSettings(certificate) });

        set.Active.ShouldBeNull();
        set.Problems.ShouldContain(problem => problem.Contains("Auth:DataProtection:Certificate") && problem.Contains("RSA"));
    }

    [Fact]
    public void Previous_certificates_without_an_active_one_are_a_problem()
    {
        using var old = TestCertificates.Create("old", NotBefore, NotAfter);

        var set = KeyRingCertificates.Load(new KeyRingCertificateOptions { PreviousCertificates = [FileSettings(old)] });

        set.Active.ShouldBeNull();
        set.Problems.ShouldContain(problem =>
            problem.Contains("Auth:DataProtection:PreviousCertificates") && problem.Contains("Auth:DataProtection:Certificate"));
    }

    [Fact]
    public void A_previous_certificate_problem_names_its_index()
    {
        using var active = TestCertificates.Create("active", NotBefore, NotAfter);

        var set = KeyRingCertificates.Load(new KeyRingCertificateOptions
        {
            Certificate = FileSettings(active),
            PreviousCertificates = [new CertificateReference { Source = CertificateSource.Store }],
        });

        set.Problems.ShouldContain(problem => problem.Contains("Auth:DataProtection:PreviousCertificates:0:Thumbprint"));
    }

    [Fact]
    public void A_thumbprint_that_is_not_in_the_store_is_a_problem()
    {
        var set = KeyRingCertificates.Load(new KeyRingCertificateOptions
        {
            Certificate = new CertificateReference
            {
                Source = CertificateSource.Store,
                Thumbprint = new string('A', 40),
                StoreLocation = StoreLocation.CurrentUser,
            },
        });

        set.Active.ShouldBeNull();
        set.Problems.ShouldContain(problem => problem.Contains("Auth:DataProtection:Certificate:Thumbprint"));
    }

    [Fact]
    public void No_settings_is_not_configured_and_has_no_problem()
    {
        var set = KeyRingCertificates.Load(new KeyRingCertificateOptions());

        set.Configured.ShouldBeFalse();
        set.Active.ShouldBeNull();
        set.Previous.ShouldBeEmpty();
        set.Problems.ShouldBeEmpty();
    }

    [Fact]
    public void Loads_from_configuration()
    {
        using var certificate = TestCertificates.Create("active", NotBefore, NotAfter);
        var path = TestCertificates.WritePfx(certificate, _directory, Password);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:DataProtection:ApplicationName"] = "Whatever",
            ["Auth:DataProtection:Certificate:Source"] = "File",
            ["Auth:DataProtection:Certificate:Path"] = path,
            ["Auth:DataProtection:Certificate:Password"] = Password,
        }).Build();

        var set = KeyRingCertificates.Load(configuration);

        set.Problems.ShouldBeEmpty();
        set.Active.ShouldNotBeNull().Thumbprint.ShouldBe(certificate.Thumbprint);
    }

    [Fact]
    public void An_unknown_source_is_a_problem_and_does_not_throw()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:DataProtection:Certificate:Source"] = "Cloud",
        }).Build();

        var set = KeyRingCertificates.Load(configuration);

        set.Active.ShouldBeNull();
        set.Problems.ShouldContain(problem => problem.Contains("Auth:DataProtection"));
    }

    [Fact]
    public void An_empty_configuration_is_not_configured()
    {
        var set = KeyRingCertificates.Load(new ConfigurationBuilder().Build());

        set.Configured.ShouldBeFalse();
        set.Problems.ShouldBeEmpty();
    }

    private CertificateReference FileSettings(X509Certificate2 certificate) =>
        new() { Source = CertificateSource.File, Path = TestCertificates.WritePfx(certificate, _directory, Password), Password = Password };
}
