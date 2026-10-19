using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.TrustedDevices;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class TrustedDeviceTests
{
    private const string Stamp = "stamp-1";

    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private static byte[] Hash(byte seed) => [.. Enumerable.Repeat(seed, 32)];

    private static TrustedDevice Trust(string deviceName = "Pixel 9", string? userAgent = "Mozilla/5.0") =>
        TrustedDevice.Trust(Guid.NewGuid(), Hash(1), deviceName, userAgent, Stamp, Lifetime, Now);

    [Fact]
    public void Trust_records_the_snapshot_and_the_absolute_expiry()
    {
        var userId = Guid.NewGuid();

        var device = TrustedDevice.Trust(userId, Hash(4), "Pixel 9", "Mozilla/5.0", Stamp, Lifetime, Now);

        device.Id.ShouldNotBe(Guid.Empty);
        device.UserId.ShouldBe(userId);
        device.TokenHash.ShouldBe(Hash(4));
        device.DeviceName.ShouldBe("Pixel 9");
        device.UserAgent.ShouldBe("Mozilla/5.0");
        device.SecurityStamp.ShouldBe(Stamp);
        device.CreatedAt.ShouldBe(Now);
        device.ExpiresAt.ShouldBe(Now + Lifetime);
        device.LastUsedAt.ShouldBeNull();
        device.RevokedAt.ShouldBeNull();
        device.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Trusted_device_is_usable_until_expiry_revocation_or_stamp_change()
    {
        var device = Trust();

        device.IsUsable(Stamp, Now).ShouldBeTrue();
        device.IsUsable(Stamp, device.ExpiresAt.AddTicks(-1)).ShouldBeTrue();
        device.IsUsable(Stamp, device.ExpiresAt).ShouldBeFalse();
        device.IsUsable("stamp-2", Now).ShouldBeFalse();
        device.IsUsable("STAMP-1", Now).ShouldBeFalse();

        device.Revoke(Now.AddDays(1));

        device.IsUsable(Stamp, Now.AddDays(1)).ShouldBeFalse();
    }

    [Fact]
    public void MarkUsed_records_the_time_without_extending_the_expiry()
    {
        var device = Trust();
        var expiry = device.ExpiresAt;
        var later = Now.AddDays(3);

        device.MarkUsed(later);

        device.LastUsedAt.ShouldBe(later);
        device.ExpiresAt.ShouldBe(expiry);
        device.IsUsable(Stamp, later).ShouldBeTrue();
    }

    [Fact]
    public void Revoke_is_idempotent_and_keeps_the_first_time()
    {
        var device = Trust();
        var first = Now.AddDays(1);

        device.Revoke(first);
        device.Revoke(first.AddDays(5));

        device.RevokedAt.ShouldBe(first);
    }

    [Fact]
    public void Trust_cuts_the_device_name_and_user_agent_to_their_columns()
    {
        var device = Trust(new string('d', UserSession.MaxDeviceNameLength + 10), new string('u', 600));

        device.DeviceName.ShouldBe(new string('d', UserSession.MaxDeviceNameLength));
        device.UserAgent.ShouldBe(new string('u', 512));
    }

    [Fact]
    public void Trust_keeps_a_missing_user_agent_null()
    {
        Trust(userAgent: null).UserAgent.ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Trust_refuses_a_token_hash_that_is_not_32_bytes(int length)
    {
        Should.Throw<ArgumentException>(() => TrustedDevice.Trust(Guid.NewGuid(), new byte[length], "Pixel 9", null, Stamp, Lifetime, Now));
    }

    [Fact]
    public void Trust_refuses_a_blank_stamp_and_a_lifetime_that_is_not_positive()
    {
        Should.Throw<ArgumentException>(() => TrustedDevice.Trust(Guid.NewGuid(), Hash(1), "Pixel 9", null, " ", Lifetime, Now));
        Should.Throw<ArgumentOutOfRangeException>(
            () => TrustedDevice.Trust(Guid.NewGuid(), Hash(1), "Pixel 9", null, Stamp, TimeSpan.Zero, Now));
    }

    [Fact]
    public void Trusted_device_not_found_error_has_the_expected_code_type_and_parameter()
    {
        var id = Guid.NewGuid();

        var error = TrustedDeviceErrors.NotFound(id);

        error.Code.ShouldBe("auth.trusted_device_not_found");
        error.Type.ShouldBe(ErrorType.NotFound);
        error.Message.ShouldBe($"Trusted device '{id}' was not found.");
        error.Parameters.ShouldNotBeNull().ShouldContainKeyAndValue("id", id);
    }
}
