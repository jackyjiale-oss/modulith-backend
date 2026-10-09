namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>Asks whether a password appears in a known data breach.</summary>
internal interface IBreachedPasswordChecker
{
    /// <summary>
    /// True when the password is known to be breached. It is false when the check is switched off or cannot be completed
    /// (fail-open); a cancelled <paramref name="cancellationToken"/> still throws.
    /// </summary>
    Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken);
}
