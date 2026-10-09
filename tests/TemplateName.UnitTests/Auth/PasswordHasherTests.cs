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

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!")]
    public void Unusable_stored_hash_still_costs_one_full_verification(string hash)
    {
        var inner = new SpyingInnerHasher(new PasswordHasherOptions { IterationCount = 10_000 });
        var hasher = new Pbkdf2PasswordHasher(inner);

        hasher.Verify(hash, Password).ShouldBe(PasswordVerification.Failed);

        // Exactly one verification ran to completion, and it was against a real (dummy) hash, not the unusable one.
        var completed = inner.CompletedVerifications.ShouldHaveSingleItem();
        completed.ShouldNotBe(hash);
        completed.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void A_usable_stored_hash_costs_one_verification()
    {
        var inner = new SpyingInnerHasher(new PasswordHasherOptions { IterationCount = 10_000 });
        var hasher = new Pbkdf2PasswordHasher(inner);
        var hash = hasher.Hash(Password);

        hasher.Verify(hash, "not the password").ShouldBe(PasswordVerification.Failed);

        inner.CompletedVerifications.ShouldHaveSingleItem().ShouldBe(hash);
    }

    private sealed class SpyingInnerHasher(PasswordHasherOptions hasherOptions)
        : PasswordHasher<Pbkdf2PasswordHasher.HashSubject>(Options.Create(hasherOptions))
    {
        /// <summary>The stored hashes whose verification ran to the end (a hash that is not base64 throws before any work).</summary>
        public List<string> CompletedVerifications { get; } = [];

        public override PasswordVerificationResult VerifyHashedPassword(
            Pbkdf2PasswordHasher.HashSubject user,
            string hashedPassword,
            string providedPassword)
        {
            var result = base.VerifyHashedPassword(user, hashedPassword, providedPassword);
            CompletedVerifications.Add(hashedPassword);
            return result;
        }
    }
}
