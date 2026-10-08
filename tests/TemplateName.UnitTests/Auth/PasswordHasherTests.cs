using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Infrastructure.Security;

namespace TemplateName.UnitTests.Auth;

public sealed class PasswordHasherTests
{
    private const string Password = "correct horse battery staple";

    // A low count keeps the unit tests fast; the default (production) count is used by the first test.
    private static Pbkdf2PasswordHasher HasherWith(int iterationCount) =>
        new(Options.Create(new PasswordHasherOptions { IterationCount = iterationCount }));

    [Fact]
    public void Hash_then_Verify_succeeds()
    {
        var hasher = new Pbkdf2PasswordHasher(Options.Create(new PasswordHasherOptions()));

        var hash = hasher.Hash(Password);

        hasher.Verify(hash, Password).ShouldBe(PasswordVerification.Success);
    }

    [Fact]
    public void Hashing_twice_gives_different_hashes()
    {
        var hasher = HasherWith(10_000);

        hasher.Hash(Password).ShouldNotBe(hasher.Hash(Password));
    }

    [Fact]
    public void Wrong_password_fails()
    {
        var hasher = HasherWith(10_000);
        var hash = hasher.Hash(Password);

        hasher.Verify(hash, "not the password").ShouldBe(PasswordVerification.Failed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a hash")]
    [InlineData("AQAAAAEAACcQAAAAEA==")]
    [InlineData("!!!not base64!!!")]
    public void Garbage_hash_returns_Failed_without_throwing(string hash)
    {
        var hasher = HasherWith(10_000);

        hasher.Verify(hash, Password).ShouldBe(PasswordVerification.Failed);
    }

    [Fact]
    public void Hash_with_lower_iteration_count_asks_for_rehash()
    {
        var hash = HasherWith(10_000).Hash(Password);
        var stricter = HasherWith(20_000);

        stricter.Verify(hash, Password).ShouldBe(PasswordVerification.SuccessRehashNeeded);
        stricter.Verify(hash, "not the password").ShouldBe(PasswordVerification.Failed);
    }

    [Fact]
    public void SpendVerificationCost_does_not_throw_for_any_password()
    {
        var hasher = HasherWith(10_000);

        Should.NotThrow(() => hasher.SpendVerificationCost(Password));
        Should.NotThrow(() => hasher.SpendVerificationCost(string.Empty));
    }
}
