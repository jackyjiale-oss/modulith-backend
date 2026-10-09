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
    ];

    private static readonly AuthNotificationTypeSource Source = new();

    public static TheoryData<string> Codes => new(AllCodes);

    [Fact]
    public void Source_declares_exactly_the_eight_auth_types()
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
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void Every_auth_type_is_security_critical_with_mandatory_email()
    {
        foreach (var type in Source.Types)
        {
            type.Category.ShouldBe(NotificationCategory.Security, type.Code);
            type.Priority.ShouldBe(NotificationPriority.Critical, type.Code);
            type.DefaultChannels.ShouldContain(NotificationChannel.Email, type.Code);
            type.MandatoryChannels.ShouldContain(NotificationChannel.Email, type.Code);
            type.IsMandatory(NotificationChannel.Email).ShouldBeTrue(type.Code);
            type.Variables.ShouldContain("display_name", type.Code);
        }
    }

    [Fact]
    public void Only_password_changed_and_token_reuse_have_an_optional_in_app_channel()
    {
        var withInApp = Source.Types.Where(type => type.DefaultChannels.Contains(NotificationChannel.InApp)).Select(type => type.Code).ToList();
        var optional = Source.Types.Where(type => type.IsConfigurable).Select(type => type.Code).ToList();

        withInApp.ShouldBe([AuthNotificationTypes.PasswordChanged, AuthNotificationTypes.TokenReuseDetected], ignoreOrder: true);
        optional.ShouldBe([AuthNotificationTypes.PasswordChanged, AuthNotificationTypes.TokenReuseDetected], ignoreOrder: true);

        foreach (var code in withInApp)
        {
            var type = Source.Types.Single(candidate => candidate.Code == code);
            type.IsMandatory(NotificationChannel.InApp).ShouldBeFalse(code);
            type.MandatoryChannels.ShouldBe([NotificationChannel.Email], code);
        }

        foreach (var type in Source.Types.Where(type => !withInApp.Contains(type.Code)))
        {
            type.DefaultChannels.ShouldBe([NotificationChannel.Email], type.Code);
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
    public void Types_declare_their_variables_and_secrets(string code, string variables, string secrets)
    {
        var type = Source.Types.Single(candidate => candidate.Code == code);

        type.Variables.ShouldBe(variables.Split(','), ignoreOrder: true);
        type.SecretVariables.ShouldBe(secrets.Split(',', StringSplitOptions.RemoveEmptyEntries), ignoreOrder: true);
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
