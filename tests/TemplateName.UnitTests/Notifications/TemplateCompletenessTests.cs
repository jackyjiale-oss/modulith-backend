using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Modules.Notifications;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Templates;

namespace TemplateName.UnitTests.Notifications;

/// <summary>
/// Decision D8 and review 8 I9: the template matrix is complete and consistent. The per-type theories run over every
/// <see cref="INotificationTypeSource"/> that <c>AddNotificationsModule</c> registers (the production catalog). They are plain theories,
/// not <c>SkipTestWithoutData</c>: an empty catalog (a source that is no longer registered) fails them instead of skipping them
/// silently, and <see cref="Production_catalog_is_not_empty"/> says so by name. The checker itself is proven against
/// <see cref="TestNotificationTypeSource"/>, where it must pass a complete type and report every planted problem.
/// </summary>
public sealed class TemplateCompletenessTests
{
    private static readonly NotificationCatalog ProductionCatalog = BuildProductionCatalog();
    private static readonly TemplateCompletenessChecker Production = new(ProductionCatalog);
    private static readonly TemplateCompletenessChecker TestSource = new(new NotificationCatalog([new TestNotificationTypeSource()]));

    public static TheoryData<string> ProductionTypeCodes => new(ProductionCatalog.All.Select(type => type.Code).Order(StringComparer.Ordinal));

    [Fact]
    public void Production_catalog_is_not_empty()
    {
        ProductionCatalog.All.ShouldNotBeEmpty("a registration bug would otherwise leave the per-type theories without data");
    }

    [Theory]
    [MemberData(nameof(ProductionTypeCodes))]
    public void Every_type_has_every_part_for_every_default_channel_and_culture(string typeCode)
    {
        var problems = Production.MissingParts(typeCode);

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Theory]
    [MemberData(nameof(ProductionTypeCodes))]
    public void Every_culture_uses_the_same_placeholders_as_en(string typeCode)
    {
        var problems = Production.PlaceholderMismatches(typeCode);

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Theory]
    [MemberData(nameof(ProductionTypeCodes))]
    public void Templates_use_only_declared_variables_and_product_name(string typeCode)
    {
        var problems = Production.UndeclaredVariables(typeCode);

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Theory]
    [MemberData(nameof(ProductionTypeCodes))]
    public void Subjects_titles_and_in_app_bodies_never_use_secret_variables(string typeCode)
    {
        var problems = Production.SecretVariableMisuse(typeCode);

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void No_orphan_template_resources()
    {
        var assemblies = ProductionCatalog.All
            .Select(type => ProductionCatalog.TemplateAssemblyOf(type.Code))
            .Append(EmbeddedTemplateStore.LayoutAssembly)
            .ToList();

        var problems = Production.OrphanResources(assemblies);

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
        ScribanResources(EmbeddedTemplateStore.LayoutAssembly).Count.ShouldBeGreaterThanOrEqualTo(RecipientCulture.Supported.Count);
    }

    [Fact]
    public void Every_culture_has_an_email_layout_that_uses_only_layout_variables()
    {
        var problems = Production.LayoutProblems();

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Layouts_are_embedded_under_the_names_the_store_reads()
    {
        // The module embeds Templates\**\*.scriban with MSBuild's default names: '_layout' and 'zh-Hans' must survive unchanged.
        var assemblyName = EmbeddedTemplateStore.LayoutAssembly.GetName().Name;

        var layouts = ScribanResources(EmbeddedTemplateStore.LayoutAssembly).Where(name => name.Contains(".Email._layout.", StringComparison.Ordinal)).ToList();

        layouts.ShouldBe(
            [
                $"{assemblyName}.Templates.Email._layout.en.html.scriban",
                $"{assemblyName}.Templates.Email._layout.ms.html.scriban",
                $"{assemblyName}.Templates.Email._layout.zh-Hans.html.scriban",
            ],
            ignoreOrder: true);
        RecipientCulture.Supported.Select(EmbeddedTemplateStore.LayoutResourceName).ShouldBe(layouts, ignoreOrder: true);
    }

    [Fact]
    public void Test_templates_are_embedded_under_the_names_the_store_reads()
    {
        var testAssembly = typeof(TestNotificationTypeSource).Assembly;

        ScribanResources(testAssembly).ShouldContain(
            EmbeddedTemplateStore.PartResourceName(testAssembly, NotificationChannel.Email, TestNotificationTypeSource.Welcome, "zh-Hans", "subject"));
        ScribanResources(testAssembly).ShouldContain($"{testAssembly.GetName().Name}.Templates.InApp.test.welcome.zh-Hans.title.scriban");
    }

    [Fact]
    public void Checker_passes_a_complete_type()
    {
        TestSource.MissingParts(TestNotificationTypeSource.Welcome).ShouldBeEmpty();
        TestSource.PlaceholderMismatches(TestNotificationTypeSource.Welcome).ShouldBeEmpty();
        TestSource.UndeclaredVariables(TestNotificationTypeSource.Welcome).ShouldBeEmpty();
        TestSource.SecretVariableMisuse(TestNotificationTypeSource.Welcome).ShouldBeEmpty();
    }

    [Fact]
    public void Checker_reports_every_planted_problem()
    {
        var prefix = $"{typeof(TestNotificationTypeSource).Assembly.GetName().Name}.Templates.InApp.test.broken";

        TestSource.MissingParts(TestNotificationTypeSource.Broken).ShouldBe([$"{prefix}.zh-Hans.body.scriban is missing"]);
        TestSource.MissingParts(TestNotificationTypeSource.EnglishOnly).Count.ShouldBe(4);
        TestSource.MissingParts(TestNotificationTypeSource.ParseError).ShouldContain(problem => problem.Contains("en.title.scriban does not parse", StringComparison.Ordinal));
        TestSource.PlaceholderMismatches(TestNotificationTypeSource.Broken).ShouldBe(
        [
            $"{prefix}.ms.title.scriban uses [display_name, user_name] but the en template uses [display_name]",
            $"{prefix}.zh-Hans.title.scriban uses [action_url] but the en template uses [display_name]",
        ]);
        TestSource.UndeclaredVariables(TestNotificationTypeSource.Broken).ShouldBe([$"{prefix}.ms.title.scriban uses the undeclared variable 'user_name'"]);
        TestSource.SecretVariableMisuse(TestNotificationTypeSource.Broken).ShouldBe([$"{prefix}.zh-Hans.title.scriban uses the secret variables [action_url]"]);

        // this.action_url names no variable, yet prints the secret.
        var thisTitle = $"{typeof(TestNotificationTypeSource).Assembly.GetName().Name}.Templates.InApp.test.this_access.en.title.scriban";
        TestSource.UndeclaredVariables(TestNotificationTypeSource.ThisAccess).ShouldBe([$"{thisTitle} uses the undeclared variable 'this'"]);
        TestSource.SecretVariableMisuse(TestNotificationTypeSource.ThisAccess).ShouldBe([$"{thisTitle} uses the secret variables [action_url]"]);
    }

    [Fact]
    public void Checker_reports_a_resource_without_a_type()
    {
        var testAssembly = typeof(TestNotificationTypeSource).Assembly;

        // The module assembly also holds Auth's templates, so Auth's source is declared too; the one undeclared test resource is the orphan.
        var checker = new TemplateCompletenessChecker(new NotificationCatalog([new TestNotificationTypeSource(), new AuthNotificationTypeSource()]));

        checker.OrphanResources([testAssembly, EmbeddedTemplateStore.LayoutAssembly]).ShouldBe(
            [$"{testAssembly.GetName().Name}.Templates.InApp.test.undeclared.en.title.scriban belongs to no declared type, channel and culture, and is not a layout"]);
    }

    private static List<string> ScribanResources(Assembly assembly) =>
        assembly.GetManifestResourceNames().Where(name => name.EndsWith(EmbeddedTemplateStore.FileExtension, StringComparison.Ordinal)).ToList();

    private static NotificationCatalog BuildProductionCatalog()
    {
        var services = new ServiceCollection();
        services.AddNotificationsModule(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        return new NotificationCatalog(provider.GetServices<INotificationTypeSource>().ToList());
    }
}
