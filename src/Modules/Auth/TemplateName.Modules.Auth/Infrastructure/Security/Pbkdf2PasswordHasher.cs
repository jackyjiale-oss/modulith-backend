using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Application.Abstractions;

namespace TemplateName.Modules.Auth.Infrastructure.Security;

/// <summary>
/// PBKDF2 hashing through the ASP.NET Core Identity <see cref="PasswordHasher{TUser}"/> (ADR 0014): a random salt per hash, the
/// algorithm and iteration count inside the hash. The iteration count comes from <see cref="PasswordHasherOptions"/>;
/// this module adds no setting for it.
/// </summary>
internal sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private static readonly HashSubject Subject = new();

    private readonly PasswordHasher<HashSubject> _inner;
    private readonly Lazy<string> _dummyHash;

    public Pbkdf2PasswordHasher(IOptions<PasswordHasherOptions> options)
    {
        _inner = new PasswordHasher<HashSubject>(options);
        _dummyHash = new Lazy<string>(
            () => _inner.HashPassword(Subject, Guid.NewGuid().ToString("N")),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Hash(string password) => _inner.HashPassword(Subject, password);

    public PasswordVerification Verify(string hash, string password)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return PasswordVerification.Failed;
        }

        try
        {
            return _inner.VerifyHashedPassword(Subject, hash, password) switch
            {
                PasswordVerificationResult.Success => PasswordVerification.Success,
                PasswordVerificationResult.SuccessRehashNeeded => PasswordVerification.SuccessRehashNeeded,
                _ => PasswordVerification.Failed,
            };
        }
        catch (FormatException)
        {
            // The stored value is not base64, so it is not a hash this hasher made.
            return PasswordVerification.Failed;
        }
    }

    public void SpendVerificationCost(string password) => _ = Verify(_dummyHash.Value, password);

    /// <summary>The Identity hasher takes a user object it never reads; this is that stand-in.</summary>
    private sealed class HashSubject;
}
