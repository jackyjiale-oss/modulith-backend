using Scriban;
using TemplateName.Modules.Notifications.Infrastructure.Templates;

namespace TemplateName.UnitTests.Notifications;

public sealed class TemplateVariablesTests
{
    [Theory]
    [InlineData("Hello {{ display_name }}, {{ product_name }}", "display_name,product_name")]
    [InlineData("{{ if display_name }}{{ action_url }}{{ else }}x{{ end }}", "action_url,display_name")]
    [InlineData("{{ $\"{action_url}\" }}", "action_url")]
    [InlineData("{{ display_name.size }} {{ display_name[0] }}", "display_name")]
    [InlineData("{{ for $i in 1..3 }}{{ $i }}{{ for.index }}{{ end }} {{ $total = 3 }}{{ $total }}", "")]
    [InlineData("{{ $action_url }} {{ $0 }} {{ $ }}", "")]
    public void Collects_the_model_variables_a_template_mentions(string text, string expected)
    {
        Collect(text).ShouldBe(Split(expected), ignoreOrder: true);
    }

    [Theory]
    [InlineData("{{ this.action_url }}")]
    [InlineData("{{ this[\"action_url\"] }}")]
    [InlineData("{{ (this).action_url }}")]
    [InlineData("{{ this }}")]
    [InlineData("{{ for $entry in this }}{{ $entry }}{{ end }}")]
    [InlineData("{{ with this }}{{ display_name }}{{ end }}")]
    [InlineData("{{ import this }}")]
    [InlineData("{{ $model = this }}{{ $model.action_url }}")]
    public void Reading_the_whole_model_through_this_is_reported(string text)
    {
        // 'this' reads every variable, secrets included, without naming one; the renderer and the completeness tests refuse it.
        Collect(text).ShouldContain(TemplateVariables.This);
    }

    [Theory]
    [InlineData("{{ if false }}{{ action_url = 'x' }}{{ end }}{{ action_url }}")]
    [InlineData("{{ for action_url in [] }}{{ end }}{{ action_url }}")]
    [InlineData("{{ if false }}{{ capture action_url }}x{{ end }}{{ end }}{{ action_url }}")]
    [InlineData("{{ func shadow(action_url) }}{{ end }}{{ action_url }}")]
    [InlineData("{{ func action_url }}{{ end }}{{ action_url }}")]
    public void A_definition_that_never_runs_does_not_hide_a_read(string text)
    {
        // A global name a template assigns, loops over, captures or declares is resolved in the model when the definition has not
        // run, so it counts as used; a template's own names are $ locals.
        Collect(text).ShouldContain("action_url");
    }

    private static IReadOnlySet<string> Collect(string text)
    {
        var template = Template.Parse(text);
        template.HasErrors.ShouldBeFalse(string.Join("; ", template.Messages));

        return TemplateVariables.Collect(template);
    }

    private static string[] Split(string names) => names.Length == 0 ? [] : names.Split(',');
}
