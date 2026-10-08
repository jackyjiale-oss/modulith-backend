using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Localization;

namespace TemplateName.Application.Common.Localization;

/// <summary>Searches the registered <see cref="ErrorMessageSource"/>s in registration order; the first one that has the code wins.</summary>
internal sealed partial class ErrorMessageLocalizer(IEnumerable<ErrorMessageSource> sources, IStringLocalizerFactory localizerFactory)
    : IErrorMessageLocalizer
{
    private readonly IStringLocalizer[] _localizers = [.. sources.Select(source => localizerFactory.Create(source.ResourceType))];

    public string Localize(string code, IReadOnlyDictionary<string, object?>? parameters, string fallback)
    {
        foreach (var localizer in _localizers)
        {
            var message = localizer[code];
            if (!message.ResourceNotFound)
            {
                return parameters is { Count: > 0 } ? Substitute(message.Value, parameters) : message.Value;
            }
        }

        return fallback;
    }

    private static string Substitute(string template, IReadOnlyDictionary<string, object?> parameters)
        => PlaceholderPattern().Replace(template, match => parameters.TryGetValue(match.Groups["name"].Value, out var value)
            ? Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty
            : match.Value);

    [GeneratedRegex(@"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex PlaceholderPattern();
}
