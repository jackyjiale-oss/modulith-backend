using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using TemplateName.SharedKernel;

namespace TemplateName.ArchitectureTests;

/// <summary>Finds the <see cref="Error"/>s an assembly defines and reads the keys of <c>*ErrorMessages</c> resources.</summary>
public static class ErrorCatalog
{
    private const BindingFlags StaticMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>
    /// The errors declared by the classes named <c>*Errors</c> in <paramref name="assembly"/>: static fields and properties of type
    /// <see cref="Error"/>, and static methods returning <see cref="Error"/>, invoked with default arguments.
    /// </summary>
    public static IReadOnlyList<Error> Collect(Assembly assembly)
    {
        var catalogues = assembly.GetTypes().Where(type => type.IsClass && type.Name.EndsWith("Errors", StringComparison.Ordinal));
        var errors = new List<Error>();

        foreach (var catalogue in catalogues)
        {
            errors.AddRange(catalogue.GetFields(StaticMembers)
                .Where(field => typeof(Error).IsAssignableFrom(field.FieldType))
                .Select(field => (Error)field.GetValue(null)!));

            errors.AddRange(catalogue.GetProperties(StaticMembers)
                .Where(property => typeof(Error).IsAssignableFrom(property.PropertyType) && property.GetIndexParameters().Length == 0)
                .Select(property => (Error)property.GetValue(null)!));

            errors.AddRange(catalogue.GetMethods(StaticMembers)
                .Where(method => typeof(Error).IsAssignableFrom(method.ReturnType) && !method.IsSpecialName && !method.ContainsGenericParameters)
                .Select(method => (Error)method.Invoke(null, [.. method.GetParameters().Select(DefaultValue)])!));
        }

        return errors;
    }

    /// <summary>The keys of the resource set for <paramref name="culture"/> itself, without falling back to a parent culture.</summary>
    public static IReadOnlySet<string> Keys(Type resource, CultureInfo culture)
        => Entries(resource, culture).Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>The entries of the resource set for <paramref name="culture"/> itself, without falling back to a parent culture.</summary>
    public static IReadOnlyDictionary<string, string> Entries(Type resource, CultureInfo culture)
    {
        var resourceSet = new ResourceManager(resource.FullName!, resource.Assembly).GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"{resource.FullName} has no resources for culture '{culture.Name}'.");

        return resourceSet.Cast<DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.Ordinal);
    }

    private static object? DefaultValue(ParameterInfo parameter)
        => parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;
}
