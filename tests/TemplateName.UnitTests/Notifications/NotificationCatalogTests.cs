using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Catalog;

namespace TemplateName.UnitTests.Notifications;

public sealed class NotificationCatalogTests
{
    private static readonly IReadOnlyList<NotificationChannel> EmailAndInApp = [NotificationChannel.Email, NotificationChannel.InApp];

    [Fact]
    public void Valid_sources_build_the_catalog()
    {
        var passwordChanged = Define("auth.password_changed", variables: ["display_name"], secretVariables: ["action_url"]);
        var welcome = Define("system.welcome", mandatory: []);
        var catalog = new NotificationCatalog([new FirstSource(passwordChanged), new SecondSource(welcome)]);

        catalog.All.Count.ShouldBe(2);
        catalog.Find("auth.password_changed").ShouldBe(passwordChanged);
        catalog.Find("system.welcome").ShouldBe(welcome);
        catalog.Find("system.unknown").ShouldBeNull();
        catalog.TemplateAssemblyOf("auth.password_changed").ShouldBe(typeof(FirstSource).Assembly);
        passwordChanged.IsMandatory(NotificationChannel.Email).ShouldBeTrue();
        passwordChanged.IsMandatory(NotificationChannel.InApp).ShouldBeFalse();
        passwordChanged.IsConfigurable.ShouldBeTrue();
        Define("auth.locked", defaults: [NotificationChannel.Email], mandatory: [NotificationChannel.Email]).IsConfigurable.ShouldBeFalse();
    }

    [Fact]
    public void An_empty_catalog_is_valid()
    {
        var catalog = new NotificationCatalog([]);

        catalog.All.ShouldBeEmpty();
        catalog.Find("auth.password_changed").ShouldBeNull();
    }

    [Fact]
    public void Unknown_code_has_no_template_assembly()
    {
        var catalog = new NotificationCatalog([new FirstSource(Define("system.welcome"))]);

        Should.Throw<InvalidOperationException>(() => catalog.TemplateAssemblyOf("system.other")).Message.ShouldContain("system.other");
    }

    [Fact]
    public void Duplicate_code_across_sources_fails_naming_both()
    {
        var catalog = new NotificationCatalog([new FirstSource(Define("system.welcome")), new SecondSource(Define("system.welcome"))]);

        var exception = Should.Throw<InvalidOperationException>(() => catalog.All);

        exception.Message.ShouldContain("system.welcome");
        exception.Message.ShouldContain(nameof(FirstSource));
        exception.Message.ShouldContain(nameof(SecondSource));
    }

    [Fact]
    public void Mandatory_channel_outside_defaults_fails()
    {
        var source = new FirstSource(Define("system.welcome", defaults: [NotificationChannel.InApp], mandatory: [NotificationChannel.Email]));
        var catalog = new NotificationCatalog([source]);

        var exception = Should.Throw<InvalidOperationException>(() => catalog.Find("system.welcome"));

        exception.Message.ShouldContain("system.welcome");
        exception.Message.ShouldContain(nameof(FirstSource));
    }

    [Fact]
    public void A_type_without_default_channels_fails()
    {
        var catalog = new NotificationCatalog([new FirstSource(Define("system.welcome", defaults: [], mandatory: []))]);

        Should.Throw<InvalidOperationException>(() => catalog.All).Message.ShouldContain("system.welcome");
    }

    [Fact]
    public void Overlapping_secret_variable_fails()
    {
        var catalog = new NotificationCatalog([new FirstSource(Define("auth.password_reset", variables: ["display_name", "action_url"], secretVariables: ["action_url"]))]);

        var exception = Should.Throw<InvalidOperationException>(() => catalog.All);

        exception.Message.ShouldContain("auth.password_reset");
        exception.Message.ShouldContain("action_url");
        exception.Message.ShouldContain(nameof(FirstSource));
    }

    [Theory]
    [InlineData("Auth.password_changed")]
    [InlineData("password_changed")]
    [InlineData("auth.")]
    [InlineData(".password_changed")]
    [InlineData("auth.password-changed")]
    [InlineData("auth.password_changed2")]
    [InlineData("auth.password.changed")]
    [InlineData("auth.password_changed\n")]
    [InlineData("")]
    public void Bad_code_fails(string code)
    {
        var catalog = new NotificationCatalog([new FirstSource(Define(code))]);

        Should.Throw<InvalidOperationException>(() => catalog.All).Message.ShouldContain(nameof(FirstSource));
    }

    [Fact]
    public void Code_longer_than_100_characters_fails()
    {
        var code = $"auth.{new string('a', 96)}";
        code.Length.ShouldBe(101);

        var catalog = new NotificationCatalog([new FirstSource(Define(code))]);

        Should.Throw<InvalidOperationException>(() => catalog.All).Message.ShouldContain(code);
        new NotificationCatalog([new FirstSource(Define(code[..100]))]).All.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("Display_name")]
    [InlineData("1name")]
    [InlineData("display-name")]
    [InlineData("display name")]
    [InlineData("")]
    public void Bad_variable_name_fails(string variable)
    {
        var catalog = new NotificationCatalog([new FirstSource(Define("system.welcome", variables: [variable]))]);

        Should.Throw<InvalidOperationException>(() => catalog.All).Message.ShouldContain("system.welcome");
    }

    [Fact]
    public void Bad_secret_variable_name_fails()
    {
        var catalog = new NotificationCatalog([new FirstSource(Define("system.welcome", secretVariables: ["Action_url"]))]);

        Should.Throw<InvalidOperationException>(() => catalog.All).Message.ShouldContain("system.welcome");
    }

    [Fact]
    public async Task Startup_check_touches_the_catalog_and_fails_the_start()
    {
        var check = new NotificationCatalogStartupCheck(new NotificationCatalog([new FirstSource(Define("Bad"))]));

        await Should.ThrowAsync<InvalidOperationException>(async () => await check.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Startup_check_passes_for_a_valid_catalog()
    {
        var check = new NotificationCatalogStartupCheck(new NotificationCatalog([new FirstSource(Define("system.welcome"))]));

        await check.StartAsync(TestContext.Current.CancellationToken);
        await check.StopAsync(TestContext.Current.CancellationToken);
    }

    private static NotificationTypeDefinition Define(
        string code,
        IReadOnlyList<NotificationChannel>? defaults = null,
        IReadOnlyList<NotificationChannel>? mandatory = null,
        string[]? variables = null,
        string[]? secretVariables = null) =>
        new(
            code,
            NotificationCategory.Security,
            NotificationPriority.Critical,
            defaults ?? EmailAndInApp,
            mandatory ?? [NotificationChannel.Email],
            new HashSet<string>(variables ?? []),
            new HashSet<string>(secretVariables ?? []));

    private sealed class FirstSource(params NotificationTypeDefinition[] types) : INotificationTypeSource
    {
        public IReadOnlyCollection<NotificationTypeDefinition> Types { get; } = types;
    }

    private sealed class SecondSource(params NotificationTypeDefinition[] types) : INotificationTypeSource
    {
        public IReadOnlyCollection<NotificationTypeDefinition> Types { get; } = types;
    }
}
