using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;

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

        user.RemovePhoneNumber(Now);

        user.PhoneNumber.ShouldBeNull();
        user.PhoneNumberVerifiedAt.ShouldBeNull();
    }

    [Fact]
    public void The_phone_number_is_masked_in_the_audit_trail()
    {
        var property = typeof(User).GetProperty(nameof(User.PhoneNumber))!;

        property.IsDefined(typeof(TemplateName.SharedKernel.Auditing.AuditMaskAttribute), inherit: false).ShouldBeTrue();
    }

    [Fact]
    public void Verifying_a_first_number_is_added_and_keeps_the_stamp()
    {
        var user = NewUser();
        var stamp = user.SecurityStamp;

        user.VerifyPhoneNumber("+60123456789", Now).ShouldBe(PhoneNumberChange.Added);

        user.SecurityStamp.ShouldBe(stamp);
        var changed = user.DomainEvents.OfType<PhoneNumberChangedDomainEvent>().ShouldHaveSingleItem();
        changed.Change.ShouldBe(PhoneNumberChange.Added);
        changed.UserId.ShouldBe(user.Id);
        changed.ChangedAt.ShouldBe(Now);
    }

    [Fact]
    public void Verifying_another_number_is_replaced_and_rotates_the_stamp()
    {
        var user = NewUser();
        user.VerifyPhoneNumber("+60198765432", Now.AddDays(-1));
        user.ClearDomainEvents();
        var stamp = user.SecurityStamp;

        user.VerifyPhoneNumber("+60123456789", Now).ShouldBe(PhoneNumberChange.Replaced);

        user.SecurityStamp.ShouldNotBe(stamp);
        user.PhoneNumber.ShouldBe("+60123456789");
        user.DomainEvents.OfType<PhoneNumberChangedDomainEvent>().ShouldHaveSingleItem().Change.ShouldBe(PhoneNumberChange.Replaced);
    }

    [Fact]
    public void Verifying_the_current_number_again_changes_nothing()
    {
        var user = NewUser();
        user.VerifyPhoneNumber("+60123456789", Now.AddDays(-1));
        user.ClearDomainEvents();
        var stamp = user.SecurityStamp;

        user.VerifyPhoneNumber("+60123456789", Now).ShouldBeNull();

        user.SecurityStamp.ShouldBe(stamp);
        user.PhoneNumberVerifiedAt.ShouldBe(Now.AddDays(-1));
        user.DomainEvents.OfType<PhoneNumberChangedDomainEvent>().ShouldBeEmpty();
    }

    [Fact]
    public void The_event_holds_only_the_last_three_digits()
    {
        var user = NewUser();

        user.VerifyPhoneNumber("+60123456789", Now);

        var changed = user.DomainEvents.OfType<PhoneNumberChangedDomainEvent>().ShouldHaveSingleItem();
        changed.LastDigits.ShouldBe("789");
        changed.ToString().ShouldNotContain("60123456789");
    }

    [Fact]
    public void Removing_a_number_rotates_the_stamp_and_raises_removed()
    {
        var user = NewUser();
        user.VerifyPhoneNumber("+60123456789", Now.AddDays(-1));
        user.ClearDomainEvents();
        var stamp = user.SecurityStamp;

        user.RemovePhoneNumber(Now).ShouldBeTrue();

        user.SecurityStamp.ShouldNotBe(stamp);
        var changed = user.DomainEvents.OfType<PhoneNumberChangedDomainEvent>().ShouldHaveSingleItem();
        changed.Change.ShouldBe(PhoneNumberChange.Removed);
        changed.LastDigits.ShouldBe("789");
    }

    [Fact]
    public void Removing_without_a_number_changes_nothing()
    {
        var user = NewUser();
        var stamp = user.SecurityStamp;

        user.RemovePhoneNumber(Now).ShouldBeFalse();

        user.SecurityStamp.ShouldBe(stamp);
        user.DomainEvents.OfType<PhoneNumberChangedDomainEvent>().ShouldBeEmpty();
    }

    private static User NewUser()
    {
        var user = User.Register("alice@example.com", "Alice", "en", "hash", Now.AddDays(-1)).Value;
        user.ClearDomainEvents();
        return user;
    }
}
