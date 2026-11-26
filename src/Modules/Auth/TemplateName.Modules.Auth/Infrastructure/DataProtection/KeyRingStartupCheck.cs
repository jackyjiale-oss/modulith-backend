using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemplateName.Modules.Auth.Infrastructure.Persistence;
using TemplateName.Modules.Auth.Infrastructure.Tokens;

namespace TemplateName.Modules.Auth.Infrastructure.DataProtection;

/// <summary>Reads the stored key ring documents; a seam so the startup check is tested without a database.</summary>
internal interface IKeyRingReader
{
    /// <summary>The XML of every document in the ring, in no particular order; empty for a ring that does not exist yet.</summary>
    Task<IReadOnlyList<string>> ReadDocumentsAsync(CancellationToken cancellationToken);
}

/// <summary>Reads <c>auth.DataProtectionKeys</c> through a scope of its own (the reader is a singleton).</summary>
internal sealed class DbKeyRingReader(IServiceScopeFactory scopes) : IKeyRingReader
{
    /// <summary>SQL Server's error for an object that does not exist: the table, before the first migration.</summary>
    private const int InvalidObjectName = 208;

    public async Task<IReadOnlyList<string>> ReadDocumentsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        try
        {
            var documents = await context.DataProtectionKeys.AsNoTracking().Select(key => key.Xml).ToListAsync(cancellationToken);
            return documents.OfType<string>().ToList();
        }
        catch (SqlException exception) when (exception.Number == InvalidObjectName)
        {
            return [];
        }
    }
}

/// <summary>
/// Refuses to start the host when the Data Protection key ring is not wrapped by a usable certificate (section <c>Auth:DataProtection</c>).
/// It does nothing in Development and Testing, where the ring may stay unwrapped. A hosted service, so the <c>migrate</c> mode, which
/// builds the host and never starts it, is not subject to it. The message lists every problem and never holds a password or key material.
/// </summary>
internal sealed class KeyRingStartupCheck(
    KeyRingCertificateSet certificates,
    IKeyRingReader ring,
    IHostEnvironment environment,
    TimeProvider time) : IHostedService
{
    private const string CertificateSetting = KeyRingCertificateOptions.SectionName + ":Certificate";
    private const string PreviousSetting = KeyRingCertificateOptions.SectionName + ":PreviousCertificates";
    private const string Runbook = "docs/runbooks/restore-backup.md";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (SigningKeyProvider.AllowsEphemeralKey(environment))
        {
            return;
        }

        var problems = new List<string>(certificates.Problems);

        // A broken or half-configured Certificate setting already has its own problem above; do not say it a second time.
        if (certificates.Active is null && !problems.Any(problem => problem.Contains(CertificateSetting, StringComparison.Ordinal)
            || problem.Contains(KeyRingCertificateOptions.SectionName + " could not be read", StringComparison.Ordinal)))
        {
            problems.Add($"{CertificateSetting} is not configured: the key ring must be wrapped with a certificate outside Development and Testing.");
        }

        if (certificates.Active is { } active)
        {
            AddValidityProblem(active, problems);
        }

        var documents = await ring.ReadDocumentsAsync(cancellationToken);
        var inspection = KeyRingInspector.Inspect(documents);
        if (inspection.PlaintextKeys > 0)
        {
            problems.Add(
                $"The key ring holds {inspection.PlaintextKeys.ToString(CultureInfo.InvariantCulture)} plaintext key(s), stored without a certificate; "
                + $"wrap the ring by configuring {CertificateSetting} and follow the runbook to replace them.");
        }

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (certificates.Active is not null)
        {
            known.Add(certificates.Active.Thumbprint);
        }

        foreach (var previous in certificates.Previous)
        {
            known.Add(previous.Thumbprint);
        }

        foreach (var thumbprint in inspection.WrappingThumbprints.Where(thumbprint => !known.Contains(thumbprint)).Order(StringComparer.Ordinal))
        {
            problems.Add(
                $"A key in the ring is wrapped under the certificate with thumbprint {thumbprint}, which is neither {CertificateSetting} nor listed in "
                + $"{PreviousSetting}; add it to {PreviousSetting}.");
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"The Data Protection key ring is not usable, so the host does not start. Check {KeyRingCertificateOptions.SectionName} ({Runbook}):"
                + string.Concat(problems.Select(problem => Environment.NewLine + "- " + problem)));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void AddValidityProblem(X509Certificate2 active, List<string> problems)
    {
        var now = time.GetUtcNow();
        var notBefore = new DateTimeOffset(active.NotBefore);
        var notAfter = new DateTimeOffset(active.NotAfter);
        if (now >= notBefore && now <= notAfter)
        {
            return;
        }

        problems.Add(
            $"{CertificateSetting} (thumbprint {active.Thumbprint}) is valid from {Format(notBefore)} to {Format(notAfter)} and it is now {Format(now)}; "
            + "use a certificate that is valid now (rotate before the expiry, see the runbook).");
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
