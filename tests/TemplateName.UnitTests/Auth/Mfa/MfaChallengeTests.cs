using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class MfaChallengeTests
{
    private const string Stamp = "stamp-1";
    private const int MaxAttempts = 5;

    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private static byte[] Hash(byte seed) => [.. Enumerable.Repeat(seed, 32)];

    private static MfaChallenge Issue(string deviceName = "Pixel 9") =>
        MfaChallenge.Issue(Guid.NewGuid(), Hash(1), Stamp, deviceName, Lifetime, Now);

    // The database counts failed attempts and consumes the challenge through conditional UPDATEs; the aggregate only reads the
    // state the repository materialized, so a test sets it the way EF Core does.
    private static void Set(MfaChallenge challenge, string property, object value) =>
        typeof(MfaChallenge).GetProperty(property)!.SetValue(challenge, value);

    [Fact]
    public void Issue_records_the_snapshot_and_the_expiry()
    {
        var userId = Guid.NewGuid();

        var challenge = MfaChallenge.Issue(userId, Hash(3), Stamp, "Pixel 9", Lifetime, Now);

        challenge.Id.ShouldNotBe(Guid.Empty);
        challenge.UserId.ShouldBe(userId);
        challenge.TokenHash.ShouldBe(Hash(3));
        challenge.SecurityStamp.ShouldBe(Stamp);
        challenge.DeviceName.ShouldBe("Pixel 9");
        challenge.CreatedAt.ShouldBe(Now);
        challenge.ExpiresAt.ShouldBe(Now + Lifetime);
        challenge.ConsumedAt.ShouldBeNull();
        challenge.FailedAttempts.ShouldBe(0);
        challenge.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Challenge_CanUse_until_expiry_attempts_consumption_or_stamp_change()
    {
        var challenge = Issue();

        challenge.CanUse(Stamp, Now, MaxAttempts).ShouldBeTrue();
        challenge.CanUse(Stamp, challenge.ExpiresAt.AddTicks(-1), MaxAttempts).ShouldBeTrue();
        challenge.CanUse(Stamp, challenge.ExpiresAt, MaxAttempts).ShouldBeFalse();
        challenge.CanUse(Stamp, challenge.ExpiresAt.AddMinutes(1), MaxAttempts).ShouldBeFalse();
        challenge.CanUse("stamp-2", Now, MaxAttempts).ShouldBeFalse();
        challenge.CanUse("STAMP-1", Now, MaxAttempts).ShouldBeFalse();

        Set(challenge, nameof(MfaChallenge.FailedAttempts), MaxAttempts - 1);
        challenge.CanUse(Stamp, Now, MaxAttempts).ShouldBeTrue();

        Set(challenge, nameof(MfaChallenge.FailedAttempts), MaxAttempts);
        challenge.CanUse(Stamp, Now, MaxAttempts).ShouldBeFalse();

        Set(challenge, nameof(MfaChallenge.FailedAttempts), 0);
        challenge.CanUse(Stamp, Now, MaxAttempts).ShouldBeTrue();

        Set(challenge, nameof(MfaChallenge.ConsumedAt), (DateTimeOffset?)Now);
        challenge.CanUse(Stamp, Now, MaxAttempts).ShouldBeFalse();
    }

    [Fact]
    public void Challenge_CanUse_is_false_when_no_attempt_is_allowed()
    {
        var challenge = Issue();

        challenge.CanUse(Stamp, Now, 1).ShouldBeTrue();
        challenge.CanUse(Stamp, Now, 0).ShouldBeFalse();
    }

    [Fact]
    public void Issue_cuts_the_device_name_to_the_session_column()
    {
        var challenge = Issue(new string('d', UserSession.MaxDeviceNameLength + 50));

        challenge.DeviceName.ShouldBe(new string('d', UserSession.MaxDeviceNameLength));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Issue_refuses_a_token_hash_that_is_not_32_bytes(int length)
    {
        Should.Throw<ArgumentException>(() => MfaChallenge.Issue(Guid.NewGuid(), new byte[length], Stamp, "Pixel 9", Lifetime, Now));
    }

    [Fact]
    public void Issue_refuses_a_blank_stamp_and_a_lifetime_that_is_not_positive()
    {
        Should.Throw<ArgumentException>(() => MfaChallenge.Issue(Guid.NewGuid(), Hash(1), " ", "Pixel 9", Lifetime, Now));
        Should.Throw<ArgumentOutOfRangeException>(() => MfaChallenge.Issue(Guid.NewGuid(), Hash(1), Stamp, "Pixel 9", TimeSpan.Zero, Now));
    }
}
