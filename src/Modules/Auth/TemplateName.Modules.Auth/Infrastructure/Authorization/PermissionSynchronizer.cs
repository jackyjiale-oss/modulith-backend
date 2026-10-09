using System.Text.RegularExpressions;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.Modules.Auth.Infrastructure.Authorization;

/// <summary>
/// Syncs the permissions every registered <see cref="IPermissionSource"/> declares into <c>auth.Permissions</c>: a new code is inserted,
/// a known one gets the declared name and description and is no longer deprecated, and a code no source declares any more is
/// deprecated, never deleted. Every definition is validated first; one bad definition fails the whole sync and changes nothing.
/// </summary>
internal sealed partial class PermissionSynchronizer(IEnumerable<IPermissionSource> sources, IPermissionRepository permissions)
{
    /// <summary>Syncs the declarations and returns every permission that is not deprecated. The caller saves the changes.</summary>
    /// <exception cref="InvalidOperationException">A definition is invalid; the message names the code and the source.</exception>
    public async Task<IReadOnlyList<Permission>> SyncAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var declared = Validate(sources);
        var existing = await permissions.ListAsync(cancellationToken);
        var byCode = existing.ToDictionary(permission => permission.Code, StringComparer.Ordinal);

        foreach (var definition in declared)
        {
            if (byCode.TryGetValue(definition.Code, out var permission))
            {
                permission.Describe(definition.Name, definition.Description);
                permission.SetDeprecated(false);
            }
            else
            {
                permission = Permission.Create(definition.Code, definition.Module, definition.Name, definition.Description, now);
                permissions.Add(permission);
                byCode.Add(permission.Code, permission);
            }
        }

        var declaredCodes = declared.Select(definition => definition.Code).ToHashSet(StringComparer.Ordinal);
        foreach (var permission in existing.Where(permission => !declaredCodes.Contains(permission.Code)))
        {
            permission.SetDeprecated(true);
        }

        return [.. byCode.Values.Where(permission => !permission.IsDeprecated)];
    }

    /// <summary>
    /// Checks every definition of every source and returns them in declaration order: the code is <c>module.resource.action</c> in
    /// lower-case snake case, the module is the code's first segment, the name is not blank, the values fit their columns, and no code is
    /// declared twice (in one source or across sources).
    /// </summary>
    /// <exception cref="InvalidOperationException">A definition is invalid; the message names the code and the source.</exception>
    internal static IReadOnlyList<PermissionDefinition> Validate(IEnumerable<IPermissionSource> sources)
    {
        var definitions = new List<PermissionDefinition>();
        var declaredBy = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            var sourceName = source.GetType().Name;
            if (source.Permissions is null)
            {
                throw new InvalidOperationException($"The permission source {sourceName} declares no permission collection.");
            }

            foreach (var definition in source.Permissions)
            {
                Validate(definition, sourceName);

                if (!declaredBy.TryAdd(definition.Code, sourceName))
                {
                    throw Invalid(definition.Code, sourceName, $"it is already declared by {declaredBy[definition.Code]}");
                }

                definitions.Add(definition);
            }
        }

        return definitions;
    }

    private static void Validate(PermissionDefinition? definition, string sourceName)
    {
        if (definition is null)
        {
            throw new InvalidOperationException($"The permission source {sourceName} declares a null permission definition.");
        }

        var code = definition.Code ?? string.Empty;
        if (!CodePattern().IsMatch(code))
        {
            throw Invalid(code, sourceName, "the code must be module.resource.action in lower-case snake case");
        }

        if (code.Length > PermissionConfiguration.CodeMaxLength)
        {
            throw Invalid(code, sourceName, $"the code is longer than {PermissionConfiguration.CodeMaxLength} characters");
        }

        var firstSegment = code[..code.IndexOf('.', StringComparison.Ordinal)];
        if (!string.Equals(definition.Module, firstSegment, StringComparison.Ordinal))
        {
            throw Invalid(code, sourceName, $"its module '{definition.Module}' is not the first segment of the code ('{firstSegment}')");
        }

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            throw Invalid(code, sourceName, "it has no name");
        }

        if (definition.Name.Length > PermissionConfiguration.NameMaxLength)
        {
            throw Invalid(code, sourceName, $"its name is longer than {PermissionConfiguration.NameMaxLength} characters");
        }

        if (definition.Description is null || definition.Description.Length > PermissionConfiguration.DescriptionMaxLength)
        {
            throw Invalid(code, sourceName, $"its description is missing or longer than {PermissionConfiguration.DescriptionMaxLength} characters");
        }
    }

    private static InvalidOperationException Invalid(string code, string sourceName, string reason)
        => new($"The permission '{code}' declared by {sourceName} is invalid: {reason}.");

    // \z, not $: $ would also match before a trailing newline.
    [GeneratedRegex(@"^[a-z]+(\.[a-z_]+){2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
