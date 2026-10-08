using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Localization;
using TemplateName.Modules.Sample.Resources;

namespace TemplateName.UnitTests.Localization;

public sealed class ErrorMessageLocalizerTests : IDisposable
{
    private readonly CultureInfo _originalUICulture = CultureInfo.CurrentUICulture;
    private readonly ServiceProvider _services = new ServiceCollection().AddLogging().AddErrorMessages<SampleErrorMessages>().BuildServiceProvider();

    private IErrorMessageLocalizer Localizer => _services.GetRequiredService<IErrorMessageLocalizer>();

    [Fact]
    public void Returns_translation_with_parameters_substituted()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ms");
        var id = Guid.NewGuid();

        var text = Localizer.Localize("leave.not_found", new Dictionary<string, object?> { ["id"] = id }, "fallback");

        text.ShouldContain(id.ToString());
        text.ShouldNotBe($"Leave request '{id}' was not found.");
    }

    [Fact]
    public void Chinese_region_falls_back_to_zh_Hans()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-Hans");
        var simplified = Localizer.Localize("leave.not_pending", null, "fallback");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");

        var region = Localizer.Localize("leave.not_pending", null, "fallback");

        region.ShouldBe(simplified);
        region.ShouldNotBe("Only a pending leave request can be approved.");
    }

    [Fact]
    public void Unknown_code_returns_fallback() => Localizer.Localize("nope.missing", null, "fb").ShouldBe("fb");

    [Fact]
    public void Unknown_placeholder_is_left_untouched()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");

        var text = Localizer.Localize("leave.not_found", null, "fallback");

        text.ShouldBe("Leave request '{id}' was not found.");
    }

    public void Dispose()
    {
        CultureInfo.CurrentUICulture = _originalUICulture;
        _services.Dispose();
    }
}
