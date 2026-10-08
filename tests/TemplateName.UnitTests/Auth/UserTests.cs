using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth;

public sealed class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    [Fact]
    public void Register_normalizes_email_and_raises_event()
    {
        var user = User.Register(" Alice@Example.COM ", "Alice", "en", "hash-1", Now).Value;

        user.Email.ShouldBe("Alice@Example.COM");
        user.NormalizedEmail.ShouldBe("ALICE@EXAMPLE.COM");
        user.DisplayName.ShouldBe("Alice");
        user.Locale.ShouldBe("en");
        user.TimeZone.ShouldBe("UTC");
        user.EmailConfirmed.ShouldBeFalse();
        user.Status.ShouldBe(UserStatus.Active);
        user.SecurityStamp.ShouldNotBeNullOrEmpty();
        user.Id.ShouldNotBe(Guid.Empty);
        user.PasswordHash.ShouldBe("hash-1");
        user.PasswordHistory.Count.ShouldBe(1);
        user.PasswordHistory.Single().PasswordHash.ShouldBe("hash-1");
        user.DomainEvents.Single().ShouldBe(new UserRegisteredDomainEvent(user.Id, "Alice@Example.COM", "en"));
    }

    [Fact]
    public void Register_without_a_password_has_no_history()
    {
        var user = User.Register("bob@example.com", "Bob", "ms", null, Now).Value;

        user.PasswordHash.ShouldBeNull();
        user.PasswordHistory.ShouldBeEmpty();
        user.PasswordChangedAt.ShouldBeNull();
    }

    [Fact]
    public void NormalizeEmail_trims_and_uppercases_invariantly()
    {
        User.NormalizeEmail("  mixed.Case@Example.com ").ShouldBe("MIXED.CASE@EXAMPLE.COM");
    }

    [Fact]
    public void ConfirmEmail_is_idempotent()
    {
        var user = NewUser();
        user.ClearDomainEvents();

        user.ConfirmEmail(Now).IsSuccess.ShouldBeTrue();
        user.ConfirmEmail(Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();

        user.EmailConfirmed.ShouldBeTrue();
        user.DomainEvents.OfType<EmailConfirmedDomainEvent>().Count().ShouldBe(1);
    }

    [Fact]
    public void EnsureCanSignIn_fails_when_email_is_not_confirmed()
    {
        var user = NewUser();

        user.EnsureCanSignIn().Error.ShouldBe(UserErrors.EmailNotVerified);
    }

    [Fact]
    public void EnsureCanSignIn_fails_when_suspended()
    {
        var user = NewConfirmedUser();
        user.Suspend(Now);

        user.EnsureCanSignIn().Error.ShouldBe(UserErrors.AccountInactive);
    }

    [Fact]
    public void EnsureCanSignIn_succeeds_for_an_active_confirmed_user()
    {
        NewConfirmedUser().EnsureCanSignIn().IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void RecordFailedSignIn_locks_at_threshold_and_raises_event()
    {
        var user = NewConfirmedUser();
        user.ClearDomainEvents();

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            user.RecordFailedSignIn(Now, 5, LockoutDuration).ShouldBeFalse();
            user.IsLockedOut(Now).ShouldBeFalse();
        }

        user.AccessFailedCount.ShouldBe(4);
        user.DomainEvents.ShouldBeEmpty();

        user.RecordFailedSignIn(Now, 5, LockoutDuration).ShouldBeTrue();

        user.LockoutEnd.ShouldBe(Now + LockoutDuration);
        user.IsLockedOut(Now).ShouldBeTrue();
        user.DomainEvents.Single().ShouldBe(new UserLockedOutDomainEvent(user.Id, Now + LockoutDuration));
    }

    [Fact]
    public void IsLockedOut_is_false_exactly_at_lockout_end()
    {
        var user = NewLockedOutUser();

        var lockoutEnd = user.LockoutEnd!.Value;

        user.IsLockedOut(lockoutEnd.AddTicks(-1)).ShouldBeTrue();
        user.IsLockedOut(lockoutEnd).ShouldBeFalse();
    }

    [Fact]
    public void RecordSuccessfulSignIn_resets_failures_and_sets_last_login()
    {
        var user = NewConfirmedUser();
        user.RecordFailedSignIn(Now, 5, LockoutDuration);
        user.RecordFailedSignIn(Now, 5, LockoutDuration);

        user.RecordSuccessfulSignIn(Now.AddMinutes(1));

        user.AccessFailedCount.ShouldBe(0);
        user.LockoutEnd.ShouldBeNull();
        user.LastLoginAt.ShouldBe(Now.AddMinutes(1));
    }

    [Fact]
    public void Failure_after_lockout_expired_starts_a_new_count()
    {
        var user = NewLockedOutUser();
        var afterExpiry = Now + LockoutDuration;

        user.RecordFailedSignIn(afterExpiry, 5, LockoutDuration).ShouldBeFalse();

        user.AccessFailedCount.ShouldBe(1);
        user.LockoutEnd.ShouldBeNull();
        user.IsLockedOut(afterExpiry).ShouldBeFalse();
    }

    [Fact]
    public void Failure_while_locked_out_changes_nothing()
    {
        var user = NewLockedOutUser();
        var lockoutEnd = user.LockoutEnd;
        user.ClearDomainEvents();

        user.RecordFailedSignIn(Now.AddMinutes(1), 5, LockoutDuration).ShouldBeFalse();

        user.LockoutEnd.ShouldBe(lockoutEnd);
        user.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void ChangePassword_rotates_stamp_clears_lockout_and_trims_history()
    {
        var user = NewLockedOutUser();
        var stamps = new HashSet<string> { user.SecurityStamp };

        for (var change = 2; change <= 5; change++)
        {
            user.ChangePassword($"hash-{change}", 3, Now.AddMinutes(change)).IsSuccess.ShouldBeTrue();
            stamps.Add(user.SecurityStamp).ShouldBeTrue();
        }

        user.PasswordHash.ShouldBe("hash-5");
        user.PasswordChangedAt.ShouldBe(Now.AddMinutes(5));
        user.AccessFailedCount.ShouldBe(0);
        user.LockoutEnd.ShouldBeNull();
        user.PasswordHistory.Select(entry => entry.PasswordHash).ShouldBe(["hash-3", "hash-4", "hash-5"]);
    }

    [Fact]
    public void ChangePassword_on_a_user_without_a_password_starts_the_history()
    {
        var user = User.Register("carol@example.com", "Carol", "en", null, Now).Value;

        user.ChangePassword("hash-1", 5, Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();

        user.PasswordHistory.Select(entry => entry.PasswordHash).ShouldBe(["hash-1"]);
    }

    [Fact]
    public void ChangePassword_raises_PasswordChangedDomainEvent()
    {
        var user = NewConfirmedUser();
        user.ClearDomainEvents();

        user.ChangePassword("hash-2", 5, Now.AddMinutes(1));

        user.DomainEvents.Single().ShouldBe(new PasswordChangedDomainEvent(user.Id, user.Email, user.Locale));
    }

    [Fact]
    public void UpgradePasswordHash_replaces_the_hash_and_nothing_else()
    {
        var user = NewLockedOutUser();
        user.ClearDomainEvents();
        var stamp = user.SecurityStamp;
        var changedAt = user.PasswordChangedAt;
        var lockoutEnd = user.LockoutEnd;

        user.UpgradePasswordHash("hash-1-stronger");

        user.PasswordHash.ShouldBe("hash-1-stronger");
        user.SecurityStamp.ShouldBe(stamp);
        user.PasswordChangedAt.ShouldBe(changedAt);
        user.LockoutEnd.ShouldBe(lockoutEnd);
        user.PasswordHistory.ShouldHaveSingleItem().PasswordHash.ShouldBe("hash-1");
        user.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void NoteLockedOut_raises_the_lockout_event_and_changes_no_state()
    {
        var user = NewConfirmedUser();
        user.ClearDomainEvents();
        var lockoutEnd = Now + LockoutDuration;

        user.NoteLockedOut(lockoutEnd);

        user.DomainEvents.ShouldHaveSingleItem().ShouldBe(new UserLockedOutDomainEvent(user.Id, lockoutEnd));
        user.AccessFailedCount.ShouldBe(0);
        user.LockoutEnd.ShouldBeNull();
    }

    [Fact]
    public void UpgradePasswordHash_refuses_a_user_without_a_password_and_a_blank_hash()
    {
        var withoutPassword = User.Register("carol@example.com", "Carol", "en", null, Now).Value;

        Should.Throw<InvalidOperationException>(() => withoutPassword.UpgradePasswordHash("hash-2"));
        Should.Throw<ArgumentException>(() => NewUser().UpgradePasswordHash(" "));
        withoutPassword.PasswordHash.ShouldBeNull();
    }

    [Fact]
    public void Suspend_rotates_stamp_and_is_idempotent()
    {
        var user = NewConfirmedUser();
        var stamp = user.SecurityStamp;

        user.Suspend(Now).IsSuccess.ShouldBeTrue();
        var suspendedStamp = user.SecurityStamp;
        user.Suspend(Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();

        user.Status.ShouldBe(UserStatus.Suspended);
        suspendedStamp.ShouldNotBe(stamp);
        user.SecurityStamp.ShouldBe(suspendedStamp);
    }

    [Fact]
    public void Reinstate_resets_failures()
    {
        var user = NewLockedOutUser();
        user.Suspend(Now);

        user.Reinstate(Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();
        user.Reinstate(Now.AddMinutes(2)).IsSuccess.ShouldBeTrue();

        user.Status.ShouldBe(UserStatus.Active);
        user.AccessFailedCount.ShouldBe(0);
        user.LockoutEnd.ShouldBeNull();
        user.IsLockedOut(Now).ShouldBeFalse();
    }

    [Fact]
    public void UpdateProfile_changes_the_profile_fields()
    {
        var user = NewUser();

        user.UpdateProfile("Alice B", "zh-Hans", "Asia/Kuala_Lumpur");

        user.DisplayName.ShouldBe("Alice B");
        user.Locale.ShouldBe("zh-Hans");
        user.TimeZone.ShouldBe("Asia/Kuala_Lumpur");
    }

    [Fact]
    public void AssignRole_twice_is_idempotent()
    {
        var user = NewUser();
        var roleId = Guid.NewGuid();
        var admin = Guid.NewGuid();

        user.AssignRole(roleId, admin, Now).IsSuccess.ShouldBeTrue();
        user.AssignRole(roleId, null, Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();

        var role = user.Roles.Single();
        role.RoleId.ShouldBe(roleId);
        role.UserId.ShouldBe(user.Id);
        role.AssignedBy.ShouldBe(admin);
        role.AssignedAt.ShouldBe(Now);
    }

    [Fact]
    public void RemoveRole_removes_an_assigned_role()
    {
        var user = NewUser();
        var roleId = Guid.NewGuid();
        user.AssignRole(roleId, null, Now);

        user.RemoveRole(roleId).IsSuccess.ShouldBeTrue();

        user.Roles.ShouldBeEmpty();
    }

    [Fact]
    public void RemoveRole_unknown_succeeds()
    {
        var user = NewUser();

        user.RemoveRole(Guid.NewGuid()).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void NoteRegistrationAttempt_raises_event_without_changing_state()
    {
        var user = NewConfirmedUser();
        user.ClearDomainEvents();
        var stamp = user.SecurityStamp;

        user.NoteRegistrationAttempt();

        user.DomainEvents.Single().ShouldBe(new RegistrationAttemptedDomainEvent(user.Id, user.Email, user.Locale));
        user.SecurityStamp.ShouldBe(stamp);
        user.EmailConfirmed.ShouldBeTrue();
        user.Status.ShouldBe(UserStatus.Active);
        user.AccessFailedCount.ShouldBe(0);
    }

    [Fact]
    public void Error_codes_and_types_match_the_contract()
    {
        Check(UserErrors.InvalidCredentials, "auth.invalid_credentials", ErrorType.Unauthorized);
        Check(UserErrors.EmailNotVerified, "auth.email_not_verified", ErrorType.Forbidden);
        Check(UserErrors.AccountInactive, "auth.account_inactive", ErrorType.Forbidden);
        Check(UserErrors.PasswordReused, "auth.password_reused", ErrorType.Validation);
        Check(UserErrors.PasswordBreached, "auth.password_breached", ErrorType.Validation);
        Check(UserErrors.CurrentPasswordIncorrect, "auth.current_password_incorrect", ErrorType.Validation);

        var id = Guid.NewGuid();
        var notFound = UserErrors.NotFound(id);

        Check(notFound, "auth.user_not_found", ErrorType.NotFound);
        notFound.Parameters.ShouldNotBeNull().ShouldContainKeyAndValue("id", id);
    }

    private static void Check(Error error, string code, ErrorType type)
    {
        error.Code.ShouldBe(code);
        error.Type.ShouldBe(type);
    }

    private static User NewUser() => User.Register("alice@example.com", "Alice", "en", "hash-1", Now).Value;

    private static User NewConfirmedUser()
    {
        var user = NewUser();
        user.ConfirmEmail(Now);
        return user;
    }

    private static User NewLockedOutUser()
    {
        var user = NewConfirmedUser();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            user.RecordFailedSignIn(Now, 5, LockoutDuration);
        }

        return user;
    }
}
