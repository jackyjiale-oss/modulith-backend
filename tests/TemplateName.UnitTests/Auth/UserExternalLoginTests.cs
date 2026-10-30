using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;

namespace TemplateName.UnitTests.Auth;

public sealed class UserExternalLoginTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static User NewUser(string? passwordHash = "hash-1")
    {
        var user = User.Register("alice@example.com", "Alice", "en", passwordHash, Now).Value;
        user.ClearDomainEvents();

        return user;
    }

    [Fact]
    public void Link_adds_a_login_and_raises_linked()
    {
        var user = NewUser();

        var result = user.LinkExternalLogin("google", "sub-1", "alice@gmail.com", Now);

        result.IsSuccess.ShouldBeTrue();
        var login = user.ExternalLogins.Single();
        result.Value.ShouldBeSameAs(login);
        login.Id.ShouldNotBe(Guid.Empty);
        login.UserId.ShouldBe(user.Id);
        login.Provider.ShouldBe("google");
        login.Subject.ShouldBe("sub-1");
        login.Email.ShouldBe("alice@gmail.com");
        login.LinkedAt.ShouldBe(Now);
        login.LastUsedAt.ShouldBeNull();
        user.DomainEvents.Single().ShouldBe(new ExternalLoginLinkedDomainEvent(user.Id, "google"));
    }

    [Fact]
    public void Link_does_not_change_the_security_stamp()
    {
        var user = NewUser();
        var stamp = user.SecurityStamp;

        user.LinkExternalLogin("google", "sub-1", null, Now).IsSuccess.ShouldBeTrue();

        user.SecurityStamp.ShouldBe(stamp);
    }

    [Fact]
    public void Link_of_the_same_identity_is_idempotent_and_raises_nothing()
    {
        var user = NewUser();
        var first = user.LinkExternalLogin("google", "sub-1", "alice@gmail.com", Now).Value;
        user.ClearDomainEvents();

        var again = user.LinkExternalLogin("Google", "sub-1", "other@gmail.com", Now.AddMinutes(5));

        again.IsSuccess.ShouldBeTrue();
        again.Value.ShouldBeSameAs(first);
        user.ExternalLogins.Count.ShouldBe(1);
        first.Email.ShouldBe("alice@gmail.com");
        first.LinkedAt.ShouldBe(Now);
        user.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Link_of_another_subject_for_the_same_provider_is_ProviderAlreadyLinked()
    {
        var user = NewUser();
        user.LinkExternalLogin("google", "sub-1", null, Now);
        user.ClearDomainEvents();

        var result = user.LinkExternalLogin("GOOGLE", "sub-2", null, Now);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ExternalLoginErrors.ProviderAlreadyLinked);
        user.ExternalLogins.Count.ShouldBe(1);
        user.ExternalLogins.Single().Subject.ShouldBe("sub-1");
        user.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Link_allows_one_login_for_each_provider()
    {
        var user = NewUser();

        user.LinkExternalLogin("google", "sub-1", null, Now).IsSuccess.ShouldBeTrue();
        user.LinkExternalLogin("apple", "sub-1", null, Now).IsSuccess.ShouldBeTrue();

        user.ExternalLogins.Select(login => login.Provider).ShouldBe(["google", "apple"], ignoreOrder: true);
    }

    [Fact]
    public void Unlink_removes_rotates_the_stamp_and_raises_unlinked()
    {
        var user = NewUser();
        var login = user.LinkExternalLogin("google", "sub-1", null, Now).Value;
        user.ClearDomainEvents();
        var stamp = user.SecurityStamp;

        var result = user.UnlinkExternalLogin(login.Id, Now);

        result.IsSuccess.ShouldBeTrue();
        user.ExternalLogins.ShouldBeEmpty();
        user.SecurityStamp.ShouldNotBe(stamp);
        user.SecurityStamp.ShouldNotBeNullOrEmpty();
        user.DomainEvents.Single().ShouldBe(new ExternalLoginUnlinkedDomainEvent(user.Id, "google"));
    }

    [Fact]
    public void Unlink_of_the_last_method_without_a_password_is_LastLoginMethod()
    {
        var user = User.RegisterExternal("bob@example.com", "Bob", "en", "google", "sub-1", "bob@example.com", Now).Value;
        user.ClearDomainEvents();
        var stamp = user.SecurityStamp;
        var login = user.ExternalLogins.Single();

        var result = user.UnlinkExternalLogin(login.Id, Now);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ExternalLoginErrors.LastLoginMethod);
        user.ExternalLogins.Count.ShouldBe(1);
        user.SecurityStamp.ShouldBe(stamp);
        user.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Unlink_with_a_password_or_another_login_succeeds()
    {
        var withPassword = NewUser();
        var onlyLogin = withPassword.LinkExternalLogin("google", "sub-1", null, Now).Value;

        withPassword.UnlinkExternalLogin(onlyLogin.Id, Now).IsSuccess.ShouldBeTrue();
        withPassword.ExternalLogins.ShouldBeEmpty();

        var passwordless = NewUser(passwordHash: null);
        var google = passwordless.LinkExternalLogin("google", "sub-1", null, Now).Value;
        passwordless.LinkExternalLogin("apple", "sub-2", null, Now);

        passwordless.UnlinkExternalLogin(google.Id, Now).IsSuccess.ShouldBeTrue();
        passwordless.ExternalLogins.Single().Provider.ShouldBe("apple");

        // Now the apple login is the last method.
        passwordless.UnlinkExternalLogin(passwordless.ExternalLogins.Single().Id, Now).Error.ShouldBe(ExternalLoginErrors.LastLoginMethod);
    }

    [Fact]
    public void Unlink_of_an_unknown_id_is_LoginNotFound()
    {
        var user = NewUser();
        user.LinkExternalLogin("google", "sub-1", null, Now);
        user.ClearDomainEvents();
        var stamp = user.SecurityStamp;

        var result = user.UnlinkExternalLogin(Guid.NewGuid(), Now);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ExternalLoginErrors.LoginNotFound);
        user.ExternalLogins.Count.ShouldBe(1);
        user.SecurityStamp.ShouldBe(stamp);
        user.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void HasPassword_follows_the_password_hash()
    {
        NewUser().HasPassword.ShouldBeTrue();
        NewUser(passwordHash: null).HasPassword.ShouldBeFalse();
    }

    [Fact]
    public void RegisterExternal_creates_a_confirmed_passwordless_user_with_the_login()
    {
        var result = User.RegisterExternal(" Carol@Example.com ", "Carol", "ms", "Google", "sub-9", "carol@gmail.com", Now);

        result.IsSuccess.ShouldBeTrue();
        var user = result.Value;
        user.Email.ShouldBe("Carol@Example.com");
        user.NormalizedEmail.ShouldBe("CAROL@EXAMPLE.COM");
        user.DisplayName.ShouldBe("Carol");
        user.Locale.ShouldBe("ms");
        user.Status.ShouldBe(UserStatus.Active);
        user.EmailConfirmed.ShouldBeTrue();
        user.HasPassword.ShouldBeFalse();
        user.PasswordHistory.ShouldBeEmpty();
        user.SecurityStamp.ShouldNotBeNullOrEmpty();

        var login = user.ExternalLogins.Single();
        login.UserId.ShouldBe(user.Id);
        login.Provider.ShouldBe("google");
        login.Subject.ShouldBe("sub-9");
        login.Email.ShouldBe("carol@gmail.com");
        login.LinkedAt.ShouldBe(Now);

        user.DomainEvents.Count.ShouldBe(2);
        user.DomainEvents.OfType<UserRegisteredDomainEvent>().Single().ShouldBe(new UserRegisteredDomainEvent(user.Id, "Carol@Example.com", "ms"));
        user.DomainEvents.OfType<EmailConfirmedDomainEvent>().Single().ShouldBe(new EmailConfirmedDomainEvent(user.Id, "Carol@Example.com"));
        user.DomainEvents.OfType<ExternalLoginLinkedDomainEvent>().ShouldBeEmpty();
    }

    [Fact]
    public void Provider_is_stored_lower_case_and_over_long_subject_is_refused()
    {
        UserExternalLogin.Create(Guid.NewGuid(), "GoOgLe", "sub-1", null, Now).Provider.ShouldBe("google");

        var longest = new string('s', UserExternalLogin.SubjectMaxLength);
        UserExternalLogin.Create(Guid.NewGuid(), "google", longest, null, Now).Subject.ShouldBe(longest);

        Should.Throw<ArgumentException>(() => UserExternalLogin.Create(Guid.NewGuid(), "google", longest + "x", null, Now));
        Should.Throw<ArgumentException>(() => UserExternalLogin.Create(Guid.NewGuid(), new string('p', UserExternalLogin.ProviderMaxLength + 1), "sub-1", null, Now));
        Should.Throw<ArgumentException>(() => UserExternalLogin.Create(Guid.NewGuid(), "google", " ", null, Now));
        Should.Throw<ArgumentException>(() => UserExternalLogin.Create(Guid.NewGuid(), " ", "sub-1", null, Now));
    }

    [Fact]
    public void Email_is_display_only_and_cut_to_its_column()
    {
        var login = UserExternalLogin.Create(Guid.NewGuid(), "google", "sub-1", new string('e', UserExternalLogin.EmailMaxLength + 20), Now);

        login.Email.ShouldBe(new string('e', UserExternalLogin.EmailMaxLength));
        UserExternalLogin.Create(Guid.NewGuid(), "google", "sub-1", "  ", Now).Email.ShouldBeNull();
    }

    [Fact]
    public void NoteUsed_records_the_time()
    {
        var login = UserExternalLogin.Create(Guid.NewGuid(), "google", "sub-1", null, Now);

        login.NoteUsed(Now.AddHours(1));

        login.LastUsedAt.ShouldBe(Now.AddHours(1));
        login.LinkedAt.ShouldBe(Now);
    }
}
