using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Modules.Notifications;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.UnitTests.Notifications;

/// <summary>The notification types Auth's events turn into (Decisions D6, D10): their security rules and their variables.</summary>
public sealed class AuthNotificationTypesTests
{
    private static readonly string[] AllCodes =
    [
        AuthNotificationTypes.EmailVerification,
        AuthNotificationTypes.PasswordReset,
        AuthNotificationTypes.PasswordResetRequired,
        AuthNotificationTypes.AccountCreated,
        AuthNotificationTypes.RegistrationAttempted,
        AuthNotificationTypes.PasswordChanged,
        AuthNotificationTypes.AccountLocked,
        AuthNotificationTypes.TokenReuseDetected,
        AuthNotificationTypes.MfaChanged,
        AuthNotificationTypes.NewDeviceLogin,
        AuthNotificationTypes.ExternalLoginLinked,
        AuthNotificationTypes.ExternalLoginUnlinked,
        AuthNotificationTypes.PhoneVerification,
        AuthNotificationTypes.PhoneSignIn,
        AuthNotificationTypes.PhoneNumberChanged,
    ];

    private static readonly string[] SmsCodes = [AuthNotificationTypes.PhoneVerification, AuthNotificationTypes.PhoneSignIn];

    private static readonly AuthNotificationTypeSource Source = new();

    public static TheoryData<string> Codes => new(AllCodes);

    [Fact]
    public void Source_declares_exactly_the_fifteen_auth_types()
    {
        Source.Types.Select(type => type.Code).ShouldBe(
            [
                "auth.email_verification",
                "auth.password_reset",
                "auth.password_reset_required",
                "auth.account_created",
                "auth.registration_attempted",
                "auth.password_changed",
                "auth.account_locked",
                "auth.token_reuse_detected",
                "auth.mfa_changed",
                "auth.new_device_login",
                "auth.external_login_linked",
                "auth.external_login_unlinked",
                "auth.phone_verification",
                "auth.phone_sign_in",
                "auth.phone_number_changed",
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void Every_auth_type_but_the_phone_codes_is_security_critical_with_mandatory_email()
    {
        foreach (var type in Source.Types.Where(type => !SmsCodes.Contains(type.Code)))
        {
            type.Category.ShouldBe(NotificationCategory.Security, type.Code);
            type.Priority.ShouldBe(NotificationPriority.Critical, type.Code);
            type.DefaultChannels.ShouldContain(NotificationChannel.Email, type.Code);
            type.MandatoryChannels.ShouldContain(NotificationChannel.Email, type.Code);
            type.IsMandatory(NotificationChannel.Email).ShouldBeTrue(type.Code);
            type.Variables.ShouldContain("display_name", type.Code);
        }
    }

    [Theory]
    [InlineData(AuthNotificationTypes.PhoneVerification, "auth.phone_verification")]
    [InlineData(AuthNotificationTypes.PhoneSignIn, "auth.phone_sign_in")]
    public void The_phone_code_types_are_critical_mandatory_sms(string code, string expected)
    {
        code.ShouldBe(expected);
        var type = Source.Types.Single(candidate => candidate.Code == code);

        type.Category.ShouldBe(NotificationCategory.Security);
        type.Priority.ShouldBe(NotificationPriority.Critical);
        type.DefaultChannels.ShouldBe([NotificationChannel.Sms]);
        type.MandatoryChannels.ShouldBe([NotificationChannel.Sms]);
        type.IsConfigurable.ShouldBeFalse();
        type.Variables.ShouldBe(["expires_in_minutes"]);
        type.SecretVariables.ShouldBe(new Dictionary<string, SecretKind> { ["code"] = SecretKind.Code });
    }

    [Theory]
    [InlineData(AuthNotificationTypes.MfaChanged, "auth.mfa_changed")]
    public void The_mfa_type_is_security_critical_email_only(string code, string expectedCode)
    {
        code.ShouldBe(expectedCode);
        var type = Source.Types.Single(candidate => candidate.Code == code);

        type.Category.ShouldBe(NotificationCategory.Security);
        type.Priority.ShouldBe(NotificationPriority.Critical);
        type.DefaultChannels.ShouldBe([NotificationChannel.Email]);
        type.MandatoryChannels.ShouldBe([NotificationChannel.Email]);
        type.IsConfigurable.ShouldBeFalse();
        type.SecretVariables.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(AuthNotificationTypes.ExternalLoginLinked, "auth.external_login_linked")]
    [InlineData(AuthNotificationTypes.ExternalLoginUnlinked, "auth.external_login_unlinked")]
    public void External_login_types_are_security_critical_with_mandatory_email_and_optional_in_app(string code, string expectedCode)
    {
        code.ShouldBe(expectedCode);
        var type = Source.Types.Single(candidate => candidate.Code == code);

        type.Category.ShouldBe(NotificationCategory.Security);
        type.Priority.ShouldBe(NotificationPriority.Critical);
        type.DefaultChannels.ShouldBe([NotificationChannel.Email, NotificationChannel.InApp]);
        type.MandatoryChannels.ShouldBe([NotificationChannel.Email]);
        type.IsConfigurable.ShouldBeTrue();
        type.SecretVariables.ShouldBeEmpty();
    }

    [Fact]
    public void Only_four_types_have_an_optional_in_app_channel()
    {
        string[] expected =
        [
            AuthNotificationTypes.PasswordChanged,
            AuthNotificationTypes.TokenReuseDetected,
            AuthNotificationTypes.ExternalLoginLinked,
            AuthNotificationTypes.ExternalLoginUnlinked,
        ];
        var withInApp = Source.Types.Where(type => type.DefaultChannels.Contains(NotificationChannel.InApp)).Select(type => type.Code).ToList();
        var optional = Source.Types.Where(type => type.IsConfigurable).Select(type => type.Code).ToList();

        withInApp.ShouldBe(expected, ignoreOrder: true);
        optional.ShouldBe([.. expected, AuthNotificationTypes.NewDeviceLogin, AuthNotificationTypes.AccountLocked], ignoreOrder: true);

        foreach (var code in withInApp)
        {
            var type = Source.Types.Single(candidate => candidate.Code == code);
            type.IsMandatory(NotificationChannel.InApp).ShouldBeFalse(code);
            type.MandatoryChannels.ShouldBe([NotificationChannel.Email], code);
        }

        foreach (var type in Source.Types.Where(type => !withInApp.Contains(type.Code) && !SmsCodes.Contains(type.Code)))
        {
            type.DefaultChannels.Where(channel => channel != NotificationChannel.Push).ShouldBe([NotificationChannel.Email], type.Code);
        }
    }

    [Fact]
    public void The_three_alert_types_offer_optional_push()
    {
        var expected = new Dictionary<string, NotificationChannel[]>
        {
            [AuthNotificationTypes.NewDeviceLogin] = [NotificationChannel.Email, NotificationChannel.Push],
            [AuthNotificationTypes.TokenReuseDetected] = [NotificationChannel.Email, NotificationChannel.InApp, NotificationChannel.Push],
            [AuthNotificationTypes.AccountLocked] = [NotificationChannel.Email, NotificationChannel.Push],
        };

        foreach (var (code, channels) in expected)
        {
            var type = Source.Types.Single(candidate => candidate.Code == code);
            type.DefaultChannels.ShouldBe(channels, ignoreOrder: true, customMessage: code);
            type.IsMandatory(NotificationChannel.Push).ShouldBeFalse(code);
            type.MandatoryChannels.ShouldBe([NotificationChannel.Email], code);
            type.IsConfigurable.ShouldBeTrue(code);
        }
    }

    [Fact]
    public void Other_auth_types_have_no_push()
    {
        string[] withPush = [AuthNotificationTypes.NewDeviceLogin, AuthNotificationTypes.TokenReuseDetected, AuthNotificationTypes.AccountLocked];

        foreach (var type in Source.Types.Where(type => !withPush.Contains(type.Code)))
        {
            type.DefaultChannels.ShouldNotContain(NotificationChannel.Push, type.Code);
        }
    }

    [Theory]
    [InlineData(AuthNotificationTypes.EmailVerification, "display_name,expires_in_minutes", "action_url")]
    [InlineData(AuthNotificationTypes.PasswordReset, "display_name,expires_in_minutes", "action_url")]
    [InlineData(AuthNotificationTypes.PasswordResetRequired, "display_name,expires_in_minutes", "action_url")]
    [InlineData(AuthNotificationTypes.AccountCreated, "display_name,expires_in_minutes", "action_url")]
    [InlineData(AuthNotificationTypes.RegistrationAttempted, "display_name", "")]
    [InlineData(AuthNotificationTypes.PasswordChanged, "display_name,occurred_at,time_zone", "")]
    [InlineData(AuthNotificationTypes.AccountLocked, "display_name,locked_until,time_zone", "")]
    [InlineData(AuthNotificationTypes.TokenReuseDetected, "display_name,occurred_at,time_zone", "")]
    [InlineData(AuthNotificationTypes.MfaChanged, "display_name,change,occurred_at,time_zone", "")]
    [InlineData(AuthNotificationTypes.NewDeviceLogin, "display_name,occurred_at,time_zone,ip_address", "")]
    [InlineData(AuthNotificationTypes.ExternalLoginLinked, "display_name,provider,occurred_at,time_zone", "")]
    [InlineData(AuthNotificationTypes.ExternalLoginUnlinked, "display_name,provider,occurred_at,time_zone", "")]
    [InlineData(AuthNotificationTypes.PhoneVerification, "expires_in_minutes", "code")]
    [InlineData(AuthNotificationTypes.PhoneSignIn, "expires_in_minutes", "code")]
    [InlineData(AuthNotificationTypes.PhoneNumberChanged, "display_name,kind,last_digits,occurred_at,time_zone", "")]
    public void Types_declare_their_variables_and_secrets(string code, string variables, string secrets)
    {
        var type = Source.Types.Single(candidate => candidate.Code == code);

        type.Variables.ShouldBe(variables.Split(','), ignoreOrder: true);
        type.SecretVariables.Keys.ShouldBe(secrets.Split(',', StringSplitOptions.RemoveEmptyEntries), ignoreOrder: true);
    }

    [Fact]
    public void The_number_change_notice_is_mandatory_email_only()
    {
        AuthNotificationTypes.PhoneNumberChanged.ShouldBe("auth.phone_number_changed");
        var type = Source.Types.Single(candidate => candidate.Code == AuthNotificationTypes.PhoneNumberChanged);

        type.DefaultChannels.ShouldBe([NotificationChannel.Email]);
        type.MandatoryChannels.ShouldBe([NotificationChannel.Email]);
        type.SecretVariables.ShouldBeEmpty();
    }

    [Fact]
    public void Constants_match_the_declared_codes()
    {
        Source.Types.Select(type => type.Code).ShouldBe(AllCodes, ignoreOrder: true);
    }

    [Fact]
    public void Module_registers_the_source_so_the_catalog_sees_every_type()
    {
        var services = new ServiceCollection();
        services.AddNotificationsModule(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        provider.GetServices<INotificationTypeSource>().OfType<AuthNotificationTypeSource>().ShouldHaveSingleItem();

        var catalog = provider.GetRequiredService<NotificationCatalog>();
        catalog.All.Select(type => type.Code).ShouldBe(AllCodes, ignoreOrder: true);
        catalog.TemplateAssemblyOf(AuthNotificationTypes.PasswordChanged).ShouldBe(typeof(NotificationsModule).Assembly);
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Catalog_accepts_each_auth_type(string code)
    {
        var catalog = new NotificationCatalog([Source]);

        catalog.Find(code).ShouldNotBeNull();
    }
}
