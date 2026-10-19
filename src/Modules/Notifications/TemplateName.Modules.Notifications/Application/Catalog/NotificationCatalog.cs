using System.Reflection;
using System.Text.RegularExpressions;

namespace TemplateName.Modules.Notifications.Application.Catalog;

/// <summary>
/// Every notification type the application declares (Decision D7: code-defined, no table). Singleton. The sources are validated the first
/// time the catalog is used and again, to fail the start rather than the first request, by <c>NotificationCatalogStartupCheck</c>.
/// </summary>
internal sealed partial class NotificationCatalog(IEnumerable<INotificationTypeSource> sources)
{
    /// <summary>The longest allowed type code, in characters.</summary>
    public const int MaxCodeLength = 100;

    private readonly Lazy<Snapshot> _snapshot = new(() => Validate(sources));

    /// <summary>Every declared type.</summary>
    /// <exception cref="InvalidOperationException">A declaration is invalid; the message names the code and the source type.</exception>
    public IReadOnlyCollection<NotificationTypeDefinition> All => _snapshot.Value.Types.Values;

    /// <summary>The type with <paramref name="code"/>, or <see langword="null"/> when none is declared.</summary>
    /// <exception cref="InvalidOperationException">A declaration is invalid.</exception>
    public NotificationTypeDefinition? Find(string code) => _snapshot.Value.Types.GetValueOrDefault(code);

    /// <summary>The assembly that holds the templates of <paramref name="code"/>: the assembly of the source that declared it.</summary>
    /// <exception cref="InvalidOperationException">The code is not declared, or a declaration is invalid.</exception>
    public Assembly TemplateAssemblyOf(string code)
    {
        return _snapshot.Value.SourceTypes.TryGetValue(code, out var sourceType)
            ? sourceType.Assembly
            : throw new InvalidOperationException($"Notification type '{code}' is not declared by any {nameof(INotificationTypeSource)}.");
    }

    private static Snapshot Validate(IEnumerable<INotificationTypeSource> sources)
    {
        var types = new Dictionary<string, NotificationTypeDefinition>(StringComparer.Ordinal);
        var sourceTypes = new Dictionary<string, Type>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            var sourceType = source.GetType();

            foreach (var type in source.Types)
            {
                ValidateType(type, sourceType);

                if (sourceTypes.TryGetValue(type.Code, out var firstSource))
                {
                    throw Invalid(type.Code, sourceType, $"is already declared by {firstSource.FullName}");
                }

                types.Add(type.Code, type);
                sourceTypes.Add(type.Code, sourceType);
            }
        }

        return new Snapshot(types, sourceTypes);
    }

    private static void ValidateType(NotificationTypeDefinition type, Type sourceType)
    {
        if (type.Code is null || type.Code.Length > MaxCodeLength || !CodePattern().IsMatch(type.Code))
        {
            throw Invalid(type.Code, sourceType, $"is not a valid code (lowercase 'area.name', letters and underscores, at most {MaxCodeLength} characters)");
        }

        if (type.DefaultChannels.Count == 0)
        {
            throw Invalid(type.Code, sourceType, "has no default channel");
        }

        var notDefault = type.MandatoryChannels.Where(channel => !type.DefaultChannels.Contains(channel)).ToList();
        if (notDefault.Count > 0)
        {
            throw Invalid(type.Code, sourceType, $"has mandatory channels that are not default channels ({string.Join(", ", notDefault)})");
        }

        foreach (var variable in type.Variables.Concat(type.SecretVariables))
        {
            if (variable is null || !VariablePattern().IsMatch(variable))
            {
                throw Invalid(type.Code, sourceType, $"has the invalid variable name '{variable}' (lowercase letters, digits and underscores, starting with a letter)");
            }
        }

        var overlap = type.Variables.Intersect(type.SecretVariables).Order(StringComparer.Ordinal).ToList();
        if (overlap.Count > 0)
        {
            throw Invalid(type.Code, sourceType, $"lists the same name as a variable and as a secret variable ({string.Join(", ", overlap)})");
        }
    }

    private static InvalidOperationException Invalid(string? code, Type sourceType, string problem) =>
        new($"Notification type '{code}' declared by {sourceType.FullName} {problem}.");

    [GeneratedRegex(@"^[a-z]+\.[a-z_]+\z")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"^[a-z][a-z0-9_]*\z")]
    private static partial Regex VariablePattern();

    private sealed record Snapshot(Dictionary<string, NotificationTypeDefinition> Types, Dictionary<string, Type> SourceTypes);
}
