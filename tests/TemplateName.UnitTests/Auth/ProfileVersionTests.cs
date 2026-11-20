using TemplateName.Modules.Auth.Application.Me;

namespace TemplateName.UnitTests.Auth;

public sealed class ProfileVersionTests
{
    [Fact]
    public void Covers_display_name_locale_and_time_zone()
    {
        var baseline = ProfileVersion.Of("Alice", "en", "UTC");

        ProfileVersion.Of("Alicia", "en", "UTC").ShouldNotBe(baseline);
        ProfileVersion.Of("Alice", "ms", "UTC").ShouldNotBe(baseline);
        ProfileVersion.Of("Alice", "en", "Asia/Kuala_Lumpur").ShouldNotBe(baseline);
        ProfileVersion.Of("Alice", "en", "UTC").ShouldBe(baseline);
    }
}
