using System.Globalization;
using System.Text.RegularExpressions;

namespace TemplateName.ArchitectureTests;

/// <summary>Enforces review Section 8 (I9): every message exists in every language, and every error code has a message.</summary>
public sealed partial class TranslationTests
{
    private static readonly CultureInfo[] Translations = [CultureInfo.GetCultureInfo("ms"), CultureInfo.GetCultureInfo("zh-Hans")];

    [Fact]
    public void Every_error_message_resource_is_checked()
    {
        var markers = Assemblies.All
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.Name.EndsWith("ErrorMessages", StringComparison.Ordinal));

        markers.ShouldBe(Assemblies.ErrorMessageResources, ignoreOrder: true);
        Assemblies.ErrorMessageResources.ShouldAllBe(type => type.Namespace!.EndsWith(".Resources", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_translation_has_the_same_keys_as_the_neutral_resource()
    {
        var failures = new List<string>();

        foreach (var resource in Assemblies.ErrorMessageResources)
        {
            var neutralKeys = ErrorCatalog.Keys(resource, CultureInfo.InvariantCulture);
            neutralKeys.ShouldNotBeEmpty(resource.Name);

            foreach (var culture in Translations)
            {
                var keys = ErrorCatalog.Keys(resource, culture);
                var missing = neutralKeys.Except(keys).Order(StringComparer.Ordinal).ToList();
                var extra = keys.Except(neutralKeys).Order(StringComparer.Ordinal).ToList();

                if (missing.Count > 0 || extra.Count > 0)
                {
                    failures.Add($"{resource.Name}.{culture.Name}: missing [{string.Join(", ", missing)}], extra [{string.Join(", ", extra)}]");
                }
            }
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Translations_use_the_same_placeholders()
    {
        var failures = new List<string>();

        foreach (var resource in Assemblies.ErrorMessageResources)
        {
            var neutral = ErrorCatalog.Entries(resource, CultureInfo.InvariantCulture);

            foreach (var culture in Translations)
            {
                foreach (var (key, text) in ErrorCatalog.Entries(resource, culture))
                {
                    if (neutral.TryGetValue(key, out var neutralText) && !Placeholders(text).SetEquals(Placeholders(neutralText)))
                    {
                        failures.Add($"{resource.Name}.{culture.Name} '{key}': '{text}' does not use the placeholders of '{neutralText}'");
                    }
                }
            }
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Every_module_error_code_has_a_neutral_message()
    {
        var failures = new List<string>();
        var codeCount = 0;

        foreach (var module in Assemblies.Modules)
        {
            var codes = ErrorCatalog.Collect(module).Select(error => error.Code).Distinct().ToList();
            codeCount += codes.Count;

            var resources = Assemblies.ErrorMessageResources.Where(resource => resource.Assembly == module).ToList();
            if (codes.Count > 0 && resources.Count != 1)
            {
                failures.Add($"{module.GetName().Name} defines error codes, so it needs exactly one *ErrorMessages resource in Assemblies.ErrorMessageResources (found {resources.Count})");
                continue;
            }

            var keys = resources.Count == 1 ? ErrorCatalog.Keys(resources[0], CultureInfo.InvariantCulture) : new HashSet<string>();
            failures.AddRange(codes.Where(code => !keys.Contains(code)).Select(code => $"{module.GetName().Name}: '{code}'"));
        }

        codeCount.ShouldBeGreaterThan(0);
        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Every_building_block_error_code_has_a_neutral_message()
    {
        var keys = Assemblies.ErrorMessageResources
            .SelectMany(resource => ErrorCatalog.Keys(resource, CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.Ordinal);

        var missing = Assemblies.BuildingBlocks
            .SelectMany(ErrorCatalog.Collect)
            .Select(error => error.Code)
            .Where(code => !keys.Contains(code))
            .Distinct();

        missing.ShouldBeEmpty();
    }

    private static HashSet<string> Placeholders(string text)
        => PlaceholderPattern().Matches(text).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\{[A-Za-z_][A-Za-z0-9_]*\}")]
    private static partial Regex PlaceholderPattern();
}
