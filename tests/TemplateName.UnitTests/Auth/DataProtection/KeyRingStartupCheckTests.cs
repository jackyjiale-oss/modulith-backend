using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Time.Testing;
using TemplateName.Modules.Auth.Infrastructure.DataProtection;

namespace TemplateName.UnitTests.Auth.DataProtection;

public sealed class KeyRingStartupCheckTests : IDisposable
{
    private const string Password = "s3cret-pfx-password";
    private const string Runbook = "docs/runbooks/restore-backup.md";
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly List<IDisposable> _disposables = [];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyring-check-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task Development_and_Testing_never_fail(string environment)
    {
        var broken = new KeyRingCertificateSet(null, [], ["Auth:DataProtection:Certificate:Path points to a file that does not exist."]);
        var check = Check(broken, environment, ["<key id=\"1\"><masterKey /></key>"]);

        await Should.NotThrowAsync(() => check.StartAsync(Ct));
    }

    [Fact]
    public async Task Production_with_no_certificate_fails_naming_the_setting()
    {
        var check = Check(new KeyRingCertificateSet(null, [], []), "Production", []);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => check.StartAsync(Ct));

        failure.Message.ShouldContain("Auth:DataProtection:Certificate");
        failure.Message.ShouldContain(Runbook);
    }

    [Fact]
    public async Task A_broken_configured_certificate_is_reported_with_its_own_problem_and_no_second_line()
    {
        var problem = "Auth:DataProtection:Certificate:Path points to a file that does not exist.";
        var check = Check(new KeyRingCertificateSet(null, [], [problem]), "Production", []);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => check.StartAsync(Ct));

        failure.Message.ShouldContain(problem);
        failure.Message.ShouldNotContain("is not configured");
    }

    [Fact]
    public async Task A_certificate_valid_only_in_the_future_fails_with_the_dates()
    {
        var certificate = Certificate("future", Now.AddDays(10), Now.AddDays(400));
        var check = Check(Set(certificate), "Production", []);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => check.StartAsync(Ct));

        failure.Message.ShouldContain("2026-06-11T12:00:00Z");
        failure.Message.ShouldContain("2027-07-06T12:00:00Z");
        failure.Message.ShouldContain("Auth:DataProtection:Certificate");
    }

    [Fact]
    public async Task A_certificate_that_expired_a_second_ago_fails()
    {
        var certificate = Certificate("expired", Now.AddDays(-400), Now.AddSeconds(-1));
        var check = Check(Set(certificate), "Production", []);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => check.StartAsync(Ct));

        failure.Message.ShouldContain("2026-06-01T11:59:59Z");
    }

    [Fact]
    public async Task A_certificate_valid_now_and_expiring_tomorrow_starts()
    {
        var certificate = Certificate("soon", Now.AddDays(-400), Now.AddDays(1));
        var check = Check(Set(certificate), "Production", KeyRingFixtures.CreateRing(certificate));

        await Should.NotThrowAsync(() => check.StartAsync(Ct));
    }

    [Fact]
    public async Task An_empty_ring_starts()
    {
        var certificate = Certificate("active", Now.AddDays(-1), Now.AddDays(365));
        var check = Check(Set(certificate), "Production", []);

        await Should.NotThrowAsync(() => check.StartAsync(Ct));
    }

    [Fact]
    public async Task A_plaintext_key_fails()
    {
        var certificate = Certificate("active", Now.AddDays(-1), Now.AddDays(365));
        var check = Check(Set(certificate), "Production", KeyRingFixtures.CreateRing(certificate: null));

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => check.StartAsync(Ct));

        failure.Message.ShouldContain("plaintext");
        failure.Message.ShouldContain(Runbook);
    }

    [Fact]
    public async Task A_key_under_an_unconfigured_certificate_fails_naming_the_thumbprint()
    {
        var active = Certificate("active", Now.AddDays(-1), Now.AddDays(365));
        var stranger = Certificate("stranger", Now.AddDays(-1), Now.AddDays(365));
        var check = Check(Set(active), "Production", KeyRingFixtures.CreateRing(stranger));

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => check.StartAsync(Ct));

        failure.Message.ShouldContain(stranger.Thumbprint);
        failure.Message.ShouldContain("Auth:DataProtection:PreviousCertificates");
    }

    [Fact]
    public async Task A_key_under_a_previous_certificate_starts()
    {
        var active = Certificate("active", Now.AddDays(-1), Now.AddDays(365));
        var previous = Certificate("previous", Now.AddDays(-500), Now.AddDays(-100));
        var documents = KeyRingFixtures.CreateRing(previous).Concat(KeyRingFixtures.CreateRing(active)).ToList();
        var check = Check(Set(active, previous), "Production", documents);

        await Should.NotThrowAsync(() => check.StartAsync(Ct));
    }

    [Fact]
    public async Task A_broken_previous_certificate_fails_even_with_an_active_one()
    {
        var active = Certificate("active", Now.AddDays(-1), Now.AddDays(365));
        var problem = "Auth:DataProtection:PreviousCertificates:0:Path points to a file that does not exist.";
        var check = Check(new KeyRingCertificateSet(active, [], [problem]), "Production", []);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => check.StartAsync(Ct));

        failure.Message.ShouldContain(problem);
    }

    [Fact]
    public async Task Every_problem_is_listed_together()
    {
        var active = Certificate("expired", Now.AddDays(-400), Now.AddDays(-1));
        var stranger = Certificate("stranger", Now.AddDays(-1), Now.AddDays(365));
        var documents = KeyRingFixtures.CreateRing(certificate: null).Concat(KeyRingFixtures.CreateRing(stranger)).ToList();
        var check = Check(Set(active), "Production", documents);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => check.StartAsync(Ct));

        failure.Message.ShouldContain("2026-05-31T12:00:00Z");
        failure.Message.ShouldContain("plaintext");
        failure.Message.ShouldContain(stranger.Thumbprint);
    }

    [Fact]
    public async Task The_message_never_contains_the_password()
    {
        // A real load: the file exists but the password is wrong, so the set carries the problem the operator will see.
        using var certificate = TestCertificates.Create("active", Now.AddDays(-1), Now.AddDays(365));
        var path = TestCertificates.WritePfx(certificate, _directory, Password);
        var wrongPassword = "wrong-" + Password;
        var set = KeyRingCertificates.Load(new KeyRingCertificateOptions
        {
            Certificate = new CertificateReference { Source = CertificateSource.File, Path = path, Password = wrongPassword },
        });
        var check = Check(set, "Production", []);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => check.StartAsync(Ct));

        failure.Message.ShouldNotContain(wrongPassword);
        failure.Message.ShouldNotContain(Password);
        failure.Message.ShouldContain("Auth:DataProtection:Certificate:Password");
    }

    [Fact]
    public async Task Stopping_does_nothing()
    {
        var check = Check(new KeyRingCertificateSet(null, [], []), "Production", []);

        await Should.NotThrowAsync(() => check.StopAsync(Ct));
    }

    private KeyRingStartupCheck Check(KeyRingCertificateSet set, string environment, IReadOnlyList<string> documents) =>
        new(set, new FakeKeyRingReader(documents), TestSigningKeys.Environment(environment), _time);

    private static KeyRingCertificateSet Set(X509Certificate2 active, params X509Certificate2[] previous) =>
        new(active, previous, []);

    private X509Certificate2 Certificate(string name, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        var certificate = TestCertificates.Create(name, notBefore, notAfter);
        _disposables.Add(certificate);
        return certificate;
    }

    private sealed class FakeKeyRingReader(IReadOnlyList<string> documents) : IKeyRingReader
    {
        public Task<IReadOnlyList<string>> ReadDocumentsAsync(CancellationToken cancellationToken) => Task.FromResult(documents);
    }
}
