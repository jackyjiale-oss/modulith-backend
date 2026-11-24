using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.UnitTests.Auth.Phone;

public sealed class UserPhoneTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void VerifyPhoneNumber_sets_both_fields()
    {
        var user = User.Register("alice@example.com", "Alice", "en", "hash", Now.AddDays(-1)).Value;

        user.VerifyPhoneNumber("+60123456789", Now);

        user.PhoneNumber.ShouldBe("+60123456789");
        user.PhoneNumberVerifiedAt.ShouldBe(Now);
    }

    [Fact]
    public void RemovePhoneNumber_clears_both()
    {
        var user = User.Register("alice@example.com", "Alice", "en", "hash", Now.AddDays(-1)).Value;
        user.VerifyPhoneNumber("+60123456789", Now);

        user.RemovePhoneNumber();

        user.PhoneNumber.ShouldBeNull();
        user.PhoneNumberVerifiedAt.ShouldBeNull();
    }

    [Fact]
    public void The_phone_number_is_masked_in_the_audit_trail()
    {
        var property = typeof(User).GetProperty(nameof(User.PhoneNumber))!;

        property.IsDefined(typeof(TemplateName.SharedKernel.Auditing.AuditMaskAttribute), inherit: false).ShouldBeTrue();
    }
}
