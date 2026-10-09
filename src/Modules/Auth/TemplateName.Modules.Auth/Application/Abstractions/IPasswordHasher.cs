namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>Hashes and verifies passwords (PBKDF2, ADR 0014).</summary>
internal interface IPasswordHasher
{
    /// <summary>Hashes a password with a new random salt, so the same password never gives the same hash twice.</summary>
    string Hash(string password);

    /// <summary>Checks a password against a stored hash. A malformed hash is <see cref="PasswordVerification.Failed"/>, never an exception.</summary>
    PasswordVerification Verify(string hash, string password);

    /// <summary>
    /// Spends the time of one verification, against a fixed dummy hash. Call it on the unknown-account path so it costs
    /// the same as a wrong password (no account enumeration by timing).
    /// </summary>
    void SpendVerificationCost(string password);
}
