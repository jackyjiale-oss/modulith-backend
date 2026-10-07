using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Serilog.Core;
using Serilog.Events;

namespace TemplateName.Web.Common.Observability;

/// <summary>
/// Replaces with <c>"***"</c> the value of every property whose name contains <c>password</c>, <c>token</c>, <c>secret</c>,
/// <c>otp</c> or <c>apikey</c> (case-insensitive) when an object is destructured with <c>{@Name}</c>. Nested objects are
/// covered because each property value goes back through the destructuring pipeline, which applies this policy again.
/// </summary>
/// <remarks>Dictionaries and other collections are left to Serilog's default handling: only the objects they contain are inspected, never their keys.</remarks>
public sealed class SensitiveDataDestructuringPolicy : IDestructuringPolicy
{
    private const string Mask = "***";

    private static readonly string[] SensitiveNameParts = ["password", "token", "secret", "otp", "apikey"];

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertiesByType = new();

    public bool TryDestructure(object value, ILogEventPropertyValueFactory propertyValueFactory, out LogEventPropertyValue result)
    {
        var type = value.GetType();
        if (!IsInspectable(type))
        {
            result = null!;
            return false;
        }

        var properties = PropertiesByType.GetOrAdd(type, ReadablePropertiesOf);
        if (!properties.Any(property => IsSensitive(property.Name)))
        {
            // Nothing to mask on this object itself; default destructuring recurses and calls this policy again for each child.
            result = null!;
            return false;
        }

        var logProperties = new List<LogEventProperty>(properties.Length);
        foreach (var property in properties)
        {
            logProperties.Add(new LogEventProperty(property.Name, IsSensitive(property.Name)
                ? new ScalarValue(Mask)
                : propertyValueFactory.CreatePropertyValue(ReadValue(property, value), destructureObjects: true)));
        }

        var typeTag = type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) ? null : type.Name;
        result = new StructureValue(logProperties, typeTag);
        return true;
    }

    private static bool IsInspectable(Type type)
        => !type.IsPrimitive
            && !type.IsEnum
            && type != typeof(string)
            && !typeof(IEnumerable).IsAssignableFrom(type)
            && !typeof(Delegate).IsAssignableFrom(type);

    private static PropertyInfo[] ReadablePropertiesOf(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .ToArray();

    private static bool IsSensitive(string propertyName)
        => SensitiveNameParts.Any(part => propertyName.Contains(part, StringComparison.OrdinalIgnoreCase));

    private static object? ReadValue(PropertyInfo property, object instance)
    {
        try
        {
            return property.GetValue(instance);
        }
        catch (TargetInvocationException exception)
        {
            return $"The property accessor threw an exception: {exception.InnerException?.GetType().Name}";
        }
    }
}
