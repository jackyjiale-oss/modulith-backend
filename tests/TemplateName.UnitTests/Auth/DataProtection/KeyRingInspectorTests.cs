using TemplateName.Modules.Auth.Infrastructure.DataProtection;

namespace TemplateName.UnitTests.Auth.DataProtection;

public sealed class KeyRingInspectorTests
{
    private static readonly DateTimeOffset NotBefore = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NotAfter = new(2036, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_ring_made_without_a_protector_has_plaintext_keys()
    {
        var documents = KeyRingFixtures.CreateRing(certificate: null, keys: 2);

        var inspection = KeyRingInspector.Inspect(documents);

        inspection.PlaintextKeys.ShouldBe(2);
        inspection.WrappingThumbprints.ShouldBeEmpty();
    }

    [Fact]
    public void A_ring_made_with_a_certificate_has_none_and_names_its_thumbprint()
    {
        using var certificate = TestCertificates.Create("active", NotBefore, NotAfter);
        var documents = KeyRingFixtures.CreateRing(certificate);

        var inspection = KeyRingInspector.Inspect(documents);

        inspection.PlaintextKeys.ShouldBe(0);
        inspection.WrappingThumbprints.ShouldBe([certificate.Thumbprint]);
    }

    [Fact]
    public void A_revocation_document_is_ignored()
    {
        using var certificate = TestCertificates.Create("active", NotBefore, NotAfter);
        var documents = KeyRingFixtures.CreateRing(certificate, revoke: true);
        documents.Count.ShouldBeGreaterThan(1, "the ring must hold a revocation document next to the key");

        var inspection = KeyRingInspector.Inspect(documents);

        inspection.PlaintextKeys.ShouldBe(0);
        inspection.WrappingThumbprints.ShouldBe([certificate.Thumbprint]);
    }

    [Fact]
    public void Several_keys_under_two_certificates_name_both_thumbprints()
    {
        using var first = TestCertificates.Create("first", NotBefore, NotAfter);
        using var second = TestCertificates.Create("second", NotBefore, NotAfter);
        var documents = KeyRingFixtures.CreateRing(first, keys: 2).Concat(KeyRingFixtures.CreateRing(second, keys: 3)).ToList();

        var inspection = KeyRingInspector.Inspect(documents);

        inspection.PlaintextKeys.ShouldBe(0);
        inspection.WrappingThumbprints.OrderBy(thumbprint => thumbprint, StringComparer.Ordinal)
            .ShouldBe(new[] { first.Thumbprint, second.Thumbprint }.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_document_that_is_not_xml_is_ignored()
    {
        var inspection = KeyRingInspector.Inspect(["not xml at all", string.Empty]);

        inspection.PlaintextKeys.ShouldBe(0);
        inspection.WrappingThumbprints.ShouldBeEmpty();
    }
}
