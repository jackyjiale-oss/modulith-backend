using System.Globalization;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Templates;

namespace TemplateName.UnitTests.Notifications;

public sealed class ScribanNotificationRendererTests
{
    private const string ProductName = "Acme Portal";
    private const string ActionUrl = "https://app.example.test/start?token=abc&lang=en";
    private const string OccurredAt = "2026-10-09 14:30 (Asia/Kuala_Lumpur)";

    // One store for the whole class, as in production (a singleton), so the tests also run against cached templates.
    private static readonly EmbeddedTemplateStore Store = new(new NotificationCatalog([new TestNotificationTypeSource()]));

    private readonly ScribanNotificationRenderer _renderer = CreateRenderer(ProductName);

    [Fact]
    public void Renders_subject_text_and_html_inside_the_layout()
    {
        var message = _renderer.Render(TestNotificationTypeSource.Welcome, NotificationChannel.Email, "en", WelcomeVariables("Aisha"));

        message.Subject.ShouldBe("Welcome to Acme Portal, Aisha");
        message.TextBody.ShouldBe($"Hello Aisha,\n\nYour account was created at {OccurredAt}. Open this link to start: {ActionUrl}");
        message.HtmlBody.ShouldNotBeNull();
        message.HtmlBody.ShouldStartWith("<!DOCTYPE html>");
        message.HtmlBody.ShouldContain("<html lang=\"en\"");
        message.HtmlBody.ShouldContain("<title>Welcome to Acme Portal, Aisha</title>");
        message.HtmlBody.ShouldContain("<p>Hello Aisha,</p>");
        message.HtmlBody.ShouldContain("<a href=\"https://app.example.test/start?token=abc&amp;lang=en\">Get started</a>");
        message.HtmlBody.ShouldContain("You received this email because you have an account with Acme Portal.");
        message.HtmlBody.ShouldNotContain("{{");
    }

    [Fact]
    public void Renders_in_app_title_and_body_without_html()
    {
        var message = _renderer.Render(TestNotificationTypeSource.Welcome, NotificationChannel.InApp, "en", WelcomeVariables("Aisha"));

        message.ShouldBe(new RenderedMessage("Welcome, Aisha", $"Your account was created at {OccurredAt}.", null));
    }

    [Fact]
    public void Html_part_encodes_display_name()
    {
        const string DisplayName = "<script>alert('x')</script> & Co";
        var renderer = CreateRenderer("Acme <Portal> & Co");

        var message = renderer.Render(TestNotificationTypeSource.Welcome, NotificationChannel.Email, "en", WelcomeVariables(DisplayName));

        message.HtmlBody.ShouldNotBeNull();
        message.HtmlBody.ShouldContain("<p>Hello &lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt; &amp; Co,</p>");
        message.HtmlBody.ShouldContain("<title>Welcome to Acme &lt;Portal&gt; &amp; Co, &lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt; &amp; Co</title>");
        message.HtmlBody.ShouldContain("an account with Acme &lt;Portal&gt; &amp; Co.");
        message.HtmlBody.ShouldNotContain("<script>");
        message.HtmlBody.ShouldNotContain("<Portal>");
        message.TextBody.ShouldStartWith($"Hello {DisplayName},");
        message.Subject.ShouldBe($"Welcome to Acme <Portal> & Co, {DisplayName}");
    }

    [Fact]
    public void Variable_values_are_never_parsed_as_templates()
    {
        const string DisplayName = "{{ 1+1 }} {{ include 'appsettings.json' }}";

        var email = _renderer.Render(TestNotificationTypeSource.Welcome, NotificationChannel.Email, "en", WelcomeVariables(DisplayName));
        var inApp = _renderer.Render(TestNotificationTypeSource.Welcome, NotificationChannel.InApp, "en", WelcomeVariables(DisplayName));

        email.Subject.ShouldBe($"Welcome to Acme Portal, {DisplayName}");
        email.TextBody.ShouldStartWith($"Hello {DisplayName},");
        email.HtmlBody.ShouldNotBeNull();
        email.HtmlBody.ShouldContain("<p>Hello {{ 1+1 }} {{ include &#39;appsettings.json&#39; }},</p>");
        inApp.Subject.ShouldBe($"Welcome, {DisplayName}");
    }

    [Fact]
    public void Missing_variable_throws_render_exception_without_values()
    {
        // Also pinned (plan, Review Focus): a template with a missing placeholder. display_name is not supplied; the other values are
        // distinctive and must not leak into the exception.
        const string Distinctive = "Zq7-distinctive-secret-value";
        var variables = new Dictionary<string, string>
        {
            ["occurred_at"] = $"{Distinctive}-time",
            ["action_url"] = $"https://app.example.test/reset?token={Distinctive}",
        };

        var exception = Should.Throw<TemplateRenderException>(
            () => _renderer.Render(TestNotificationTypeSource.Welcome, NotificationChannel.Email, "en", variables));

        exception.TypeCode.ShouldBe(TestNotificationTypeSource.Welcome);
        exception.Channel.ShouldBe(NotificationChannel.Email);
        exception.Culture.ShouldBe("en");
        exception.Reason.ShouldContain("display_name");
        exception.Message.ShouldContain(TestNotificationTypeSource.Welcome);
        exception.Message.ShouldContain("display_name");
        exception.Message.ShouldNotContain(Distinctive);
        exception.Reason.ShouldNotContain(Distinctive);
        exception.ToString().ShouldNotContain(Distinctive);
    }

    [Fact]
    public void Missing_culture_falls_back_to_en()
    {
        var variables = new Dictionary<string, string> { ["display_name"] = "Aisha" };

        var malay = _renderer.Render(TestNotificationTypeSource.EnglishOnly, NotificationChannel.InApp, "ms", variables);
        var chinese = _renderer.Render(TestNotificationTypeSource.EnglishOnly, NotificationChannel.InApp, "zh-Hans", variables);

        malay.ShouldBe(new RenderedMessage("English title for Aisha", "English body", null));
        chinese.ShouldBe(malay);
    }

    [Fact]
    public void Unsupported_culture_renders_en()
    {
        var message = _renderer.Render(TestNotificationTypeSource.Welcome, NotificationChannel.Email, "fr-FR", WelcomeVariables("Aisha"));

        message.Subject.ShouldBe("Welcome to Acme Portal, Aisha");
        message.HtmlBody.ShouldNotBeNull();
        message.HtmlBody.ShouldContain("<html lang=\"en\"");
    }

    [Fact]
    public void Missing_en_template_throws()
    {
        var variables = new Dictionary<string, string>();

        var exception = Should.Throw<TemplateRenderException>(
            () => _renderer.Render(TestNotificationTypeSource.NoEnglish, NotificationChannel.InApp, "zh-Hans", variables));

        exception.Culture.ShouldBe("zh-Hans");
        exception.Reason.ShouldContain("title");
        exception.Reason.ShouldContain("'zh-Hans'");
        exception.Reason.ShouldContain("'en'");
        _renderer.Render(TestNotificationTypeSource.NoEnglish, NotificationChannel.InApp, "ms", variables)
            .ShouldBe(new RenderedMessage("Tajuk Melayu", "Kandungan Melayu", null));
    }

    [Fact]
    public void Rendering_ignores_current_ui_culture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUICulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ms");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ms");

            var message = _renderer.Render(TestNotificationTypeSource.Welcome, NotificationChannel.Email, "zh-Hans", WelcomeVariables("李明"));

            message.Subject.ShouldBe("欢迎使用 Acme Portal，李明");
            message.TextBody.ShouldStartWith("李明，您好：");
            message.HtmlBody.ShouldNotBeNull();
            message.HtmlBody.ShouldContain("<html lang=\"zh-Hans\"");
            message.HtmlBody.ShouldContain("<p>李明，您好：</p>");
            message.HtmlBody.ShouldContain("Acme Portal 账户");
            message.HtmlBody.ShouldNotContain("Anda menerima");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUICulture;
        }
    }

    [Fact]
    public void Numbers_and_dates_format_invariantly()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUICulture = CultureInfo.CurrentUICulture;
        try
        {
            // de-DE writes 1234,5; the template must not.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            var variables = new Dictionary<string, string> { ["occurred_at"] = "2026-10-09T14:30:00+08:00" };

            var message = _renderer.Render(TestNotificationTypeSource.Formatting, NotificationChannel.InApp, "en", variables);

            message.Subject.ShouldBe("1234.5 and 1.25");
            message.TextBody.ShouldBe("Occurred at 2026-10-09T14:30:00+08:00");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUICulture;
        }
    }

    [Fact]
    public void Subject_is_single_line_and_capped()
    {
        var displayName = "Line1\r\nLine2\nLine3\rLine4\u2028Line5\tEnd" + new string('x', 400);

        var email = _renderer.Render(TestNotificationTypeSource.Welcome, NotificationChannel.Email, "en", WelcomeVariables(displayName));
        var inApp = _renderer.Render(TestNotificationTypeSource.Welcome, NotificationChannel.InApp, "en", WelcomeVariables(displayName));

        email.Subject.Length.ShouldBe(ScribanNotificationRenderer.MaxSubjectLength);
        email.Subject.ShouldStartWith("Welcome to Acme Portal, Line1 Line2 Line3 Line4 Line5 Endxxx");
        inApp.Subject.Length.ShouldBe(ScribanNotificationRenderer.MaxSubjectLength);
        inApp.Subject.ShouldStartWith("Welcome, Line1 Line2 Line3 Line4 Line5 Endxxx");
        foreach (var subject in new[] { email.Subject, inApp.Subject })
        {
            subject.ShouldNotContain('\r');
            subject.ShouldNotContain('\n');
            subject.ShouldNotContain('\u2028');
            subject.ShouldNotContain('\t');
        }

        email.TextBody.ShouldContain("Line1\r\nLine2\nLine3");
        ScribanNotificationRenderer.MaxSubjectLength.ShouldBe(300);
    }

    [Theory]
    [InlineData(TestNotificationTypeSource.IncludeAttempt, "uses variables that were not supplied: include")]
    [InlineData(TestNotificationTypeSource.ImportAttempt, "failed while rendering at line 1, column ")]
    [InlineData(TestNotificationTypeSource.BuiltinCall, "uses variables that were not supplied: date, object")]
    [InlineData(TestNotificationTypeSource.MemberAccess, "failed while rendering")]
    [InlineData(TestNotificationTypeSource.ThisAccess, "uses variables that were not supplied: this")]
    [InlineData(TestNotificationTypeSource.RunawayLoop, "failed while rendering")]
    [InlineData(TestNotificationTypeSource.RunawayRecursion, "uses variables that were not supplied: again")]
    public void Template_cannot_leave_the_sandbox(string typeCode, string expectedReason)
    {
        // No file loader, no built-in functions, no .NET members, bounded loops and recursion: each fails in the title as a render
        // exception that names the type and never echoes the values.
        const string Distinctive = "Zq7-distinctive-display-name";
        var variables = new Dictionary<string, string> { ["display_name"] = Distinctive, ["action_url"] = Distinctive, ["this"] = Distinctive };

        var exception = Should.Throw<TemplateRenderException>(() => _renderer.Render(typeCode, NotificationChannel.InApp, "en", variables));

        exception.TypeCode.ShouldBe(typeCode);
        exception.Reason.ShouldStartWith($"{typeof(TestNotificationTypeSource).Assembly.GetName().Name}.Templates.InApp.{typeCode}.en.title.scriban ");
        exception.Reason.ShouldContain(expectedReason);
        exception.Message.ShouldContain(typeCode);
        exception.ToString().ShouldNotContain(Distinctive);
    }

    [Fact]
    public void Parse_error_throws_render_exception_with_the_parser_message()
    {
        const string Distinctive = "Zq7-distinctive-display-name";
        var variables = new Dictionary<string, string> { ["display_name"] = Distinctive };

        var first = Should.Throw<TemplateRenderException>(
            () => _renderer.Render(TestNotificationTypeSource.ParseError, NotificationChannel.InApp, "en", variables));
        var second = Should.Throw<TemplateRenderException>(
            () => _renderer.Render(TestNotificationTypeSource.ParseError, NotificationChannel.InApp, "en", variables));

        first.Reason.ShouldContain("test.parse_error.en.title.scriban");
        first.Reason.ShouldContain("does not parse");
        first.ToString().ShouldNotContain(Distinctive);
        second.Reason.ShouldBe(first.Reason);
    }

    [Fact]
    public void Unknown_type_throws_render_exception()
    {
        var exception = Should.Throw<TemplateRenderException>(
            () => _renderer.Render("test.unknown", NotificationChannel.Email, "en", new Dictionary<string, string>()));

        exception.TypeCode.ShouldBe("test.unknown");
        exception.Reason.ShouldContain("not declared");
    }

    [Fact]
    public void Concurrent_renders_return_their_own_values()
    {
        var store = new EmbeddedTemplateStore(new NotificationCatalog([new TestNotificationTypeSource()]));
        var renderer = new ScribanNotificationRenderer(store, Options.Create(new TemplateOptions { ProductName = ProductName }));

        var subjects = new string[64];
        Parallel.For(0, subjects.Length, i =>
            subjects[i] = renderer.Render(TestNotificationTypeSource.Welcome, NotificationChannel.Email, "ms", WelcomeVariables($"User {i}")).Subject);

        for (var i = 0; i < subjects.Length; i++)
        {
            subjects[i].ShouldBe($"Selamat datang ke Acme Portal, User {i}");
        }
    }

    private static ScribanNotificationRenderer CreateRenderer(string productName) =>
        new(Store, Options.Create(new TemplateOptions { ProductName = productName }));

    private static Dictionary<string, string> WelcomeVariables(string displayName) => new()
    {
        ["display_name"] = displayName,
        ["occurred_at"] = OccurredAt,
        ["action_url"] = ActionUrl,
    };
}
