using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TemplateName.SharedKernel;
using TemplateName.SharedKernel.Auditing;

namespace TemplateName.Infrastructure.Common.Auditing;

/// <summary>
/// Turns the pending changes of a <see cref="ChangeTracker"/> into the <see cref="EntityChange"/> list of one save. Only entries of
/// <see cref="AuditedAttribute">audited</see> entity types in the state Added, Modified or Deleted are looked at. It reads the
/// tracker and nothing else: not the current user, not the clock and not the outbox.
/// </summary>
internal static class EntityChangeExtractor
{
    /// <summary>The longest text value written to <c>Changes</c>; a longer one is cut and ends in an ellipsis.</summary>
    internal const int MaxValueLength = 4000;

    /// <summary>What a changed <see cref="AuditMaskAttribute">masked</see> property is written as, in place of its value.</summary>
    internal const string MaskedValue = "****";

    private const string Ellipsis = "…";
    private const string OldName = "old";
    private const string NewName = "new";

    private static readonly string[] AuditableColumns =
    [
        nameof(IAuditable.CreatedAt),
        nameof(IAuditable.CreatedBy),
        nameof(IAuditable.UpdatedAt),
        nameof(IAuditable.UpdatedBy),
    ];

    private static readonly string[] SoftDeletableColumns =
    [
        nameof(ISoftDeletable.IsDeleted),
        nameof(ISoftDeletable.DeletedAt),
        nameof(ISoftDeletable.DeletedBy),
    ];

    // The text is JSON stored in an nvarchar column, never placed in HTML, so non-ASCII characters are kept readable.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Decided once per entity type: whether it is audited, and which of its properties are.
    private static readonly ConcurrentDictionary<IEntityType, AuditedEntityShape> Shapes = new();

    /// <summary>Returns one <see cref="EntityChange"/> for every audited entry that holds an audited change, in the tracker's order.</summary>
    /// <param name="changeTracker">The tracker of the saving context, after changes are detected.</param>
    /// <exception cref="InvalidOperationException">An added audited entity has a key that is a temporary store value.</exception>
    internal static IReadOnlyList<EntityChange> Extract(ChangeTracker changeTracker)
    {
        List<EntityChange> changes = [];
        foreach (var entry in changeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var shape = Shapes.GetOrAdd(entry.Metadata, CreateShape);
            if (!shape.IsAudited)
            {
                continue;
            }

            var change = ExtractEntry(entry, shape);
            if (change is not null)
            {
                changes.Add(change);
            }
        }

        return changes;
    }

    private static AuditedEntityShape CreateShape(IEntityType entityType)
    {
        var clrType = entityType.ClrType;
        if (!clrType.IsDefined(typeof(AuditedAttribute), inherit: false))
        {
            return AuditedEntityShape.NotAudited;
        }

        HashSet<string> skippedNames = [];
        if (typeof(IAuditable).IsAssignableFrom(clrType))
        {
            skippedNames.UnionWith(AuditableColumns);
        }

        var isSoftDeletable = typeof(ISoftDeletable).IsAssignableFrom(clrType);
        if (isSoftDeletable)
        {
            skippedNames.UnionWith(SoftDeletableColumns);
        }

        // A store-generated value (a row version, a computed column) is not known when a save starts, or changes in every save.
        var properties = entityType.GetProperties()
            .Where(property => property.ValueGenerated == ValueGenerated.Never
                && !skippedNames.Contains(property.Name)
                && property.PropertyInfo?.IsDefined(typeof(AuditIgnoreAttribute), inherit: true) != true)
            .Select(property => new AuditedProperty(
                property,
                property.PropertyInfo?.IsDefined(typeof(AuditMaskAttribute), inherit: true) == true,
                property.GetValueConverter()))
            .ToList();

        return new AuditedEntityShape(
            IsAudited: true,
            Properties: properties,
            KeyProperties: entityType.FindPrimaryKey()!.Properties,
            IsDeletedProperty: isSoftDeletable ? entityType.FindProperty(nameof(ISoftDeletable.IsDeleted)) : null);
    }

    private static EntityChange? ExtractEntry(EntityEntry entry, AuditedEntityShape shape)
    {
        var entityType = entry.Metadata.ClrType.Name;
        if (entry.State == EntityState.Added)
        {
            ThrowIfKeyIsTemporary(entry, shape, entityType);
        }

        var action = entry.State switch
        {
            EntityState.Added => EntityChangeAction.Created,
            EntityState.Deleted => EntityChangeAction.Deleted,
            _ => ResolveModifiedAction(entry, shape),
        };

        var buffer = new ArrayBufferWriter<byte>();
        int changedCount;
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            changedCount = entry.State switch
            {
                EntityState.Added => WriteSingleSided(writer, shape, entry.CurrentValues, NewName),
                EntityState.Deleted => WriteSingleSided(writer, shape, entry.OriginalValues, OldName),
                _ => WriteDifferences(writer, shape, entry.OriginalValues, entry.CurrentValues),
            };
            writer.WriteEndObject();
        }

        // A save that touched only ignored, stamp or store-generated columns leaves nothing to record.
        if (action == EntityChangeAction.Updated && changedCount == 0)
        {
            return null;
        }

        return new EntityChange(entityType, BuildEntityId(entry, shape), action, Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static EntityChangeAction ResolveModifiedAction(EntityEntry entry, AuditedEntityShape shape)
    {
        if (shape.IsDeletedProperty is null)
        {
            return EntityChangeAction.Updated;
        }

        var wasDeleted = entry.OriginalValues[shape.IsDeletedProperty] is true;
        var isDeleted = entry.CurrentValues[shape.IsDeletedProperty] is true;
        return (wasDeleted, isDeleted) switch
        {
            (false, true) => EntityChangeAction.SoftDeleted,
            (true, false) => EntityChangeAction.Restored,
            _ => EntityChangeAction.Updated,
        };
    }

    private static void ThrowIfKeyIsTemporary(EntityEntry entry, AuditedEntityShape shape, string entityType)
    {
        foreach (var key in shape.KeyProperties)
        {
            if (entry.Property(key.Name).IsTemporary)
            {
                throw new InvalidOperationException(
                    $"The audited entity type {entityType} has a key ({key.Name}) that the database generates, so its value is not known when "
                    + "the save starts. Give the key an application-generated value (ValueGeneratedNever and SequentialGuid.Create).");
            }
        }
    }

    private static string BuildEntityId(EntityEntry entry, AuditedEntityShape shape)
    {
        var values = entry.CurrentValues;
        if (shape.KeyProperties.Count == 1)
        {
            return FormatKeyValue(values[shape.KeyProperties[0]]);
        }

        return string.Join(';', shape.KeyProperties.Select(key => $"{key.Name}={FormatKeyValue(values[key])}"));
    }

    private static string FormatKeyValue(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    // Created and Deleted: every audited property that is not null, with the one side the action has.
    private static int WriteSingleSided(Utf8JsonWriter writer, AuditedEntityShape shape, PropertyValues values, string sideName)
    {
        var count = 0;
        foreach (var property in shape.Properties)
        {
            var value = ToStoredValue(property, values[property.Property]);
            if (value is null)
            {
                continue;
            }

            writer.WriteStartObject(property.Property.Name);
            WriteSide(writer, sideName, property, value);
            writer.WriteEndObject();
            count++;
        }

        return count;
    }

    // Updated, SoftDeleted and Restored: only the audited properties whose stored value differs, as old and new.
    private static int WriteDifferences(Utf8JsonWriter writer, AuditedEntityShape shape, PropertyValues originalValues, PropertyValues currentValues)
    {
        var count = 0;
        foreach (var property in shape.Properties)
        {
            var oldValue = ToStoredValue(property, originalValues[property.Property]);
            var newValue = ToStoredValue(property, currentValues[property.Property]);
            if (StructuralComparisons.StructuralEqualityComparer.Equals(oldValue, newValue))
            {
                continue;
            }

            writer.WriteStartObject(property.Property.Name);
            WriteSide(writer, OldName, property, oldValue);
            WriteSide(writer, NewName, property, newValue);
            writer.WriteEndObject();
            count++;
        }

        return count;
    }

    private static object? ToStoredValue(AuditedProperty property, object? value) =>
        value is null || property.Converter is null ? value : property.Converter.ConvertToProvider(value);

    private static void WriteSide(Utf8JsonWriter writer, string sideName, AuditedProperty property, object? value)
    {
        writer.WritePropertyName(sideName);
        if (value is not null && property.IsMasked)
        {
            writer.WriteStringValue(MaskedValue);
            return;
        }

        WriteValue(writer, value);
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(Truncate(text));
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int or short or sbyte or byte or ushort or uint:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case ulong number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case double number when double.IsFinite(number):
                writer.WriteNumberValue(number);
                break;
            case float number when float.IsFinite(number):
                writer.WriteNumberValue(number);
                break;
            case Guid identifier:
                writer.WriteStringValue(identifier);
                break;
            case DateTime moment:
                // An unspecified value is UTC, as UtcDateTimeConverter reads it.
                writer.WriteStringValue(moment.Kind == DateTimeKind.Local ? moment.ToUniversalTime() : DateTime.SpecifyKind(moment, DateTimeKind.Utc));
                break;
            case DateTimeOffset moment:
                writer.WriteStringValue(moment.ToUniversalTime());
                break;
            case DateOnly date:
                writer.WriteStringValue(date.ToString("O", CultureInfo.InvariantCulture));
                break;
            case TimeOnly time:
                writer.WriteStringValue(time.ToString("O", CultureInfo.InvariantCulture));
                break;
            case TimeSpan duration:
                writer.WriteStringValue(duration.ToString("c", CultureInfo.InvariantCulture));
                break;
            case Enum member:
                writer.WriteStringValue(member.ToString());
                break;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;
            default:
                writer.WriteStringValue(Truncate(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));
                break;
        }
    }

    private static string Truncate(string text)
    {
        if (text.Length <= MaxValueLength)
        {
            return text;
        }

        var keptLength = MaxValueLength - Ellipsis.Length;

        // Do not leave half of a surrogate pair at the cut.
        if (char.IsHighSurrogate(text[keptLength - 1]))
        {
            keptLength--;
        }

        return string.Concat(text.AsSpan(0, keptLength), Ellipsis);
    }

    private sealed record AuditedProperty(IProperty Property, bool IsMasked, ValueConverter? Converter);

    private sealed record AuditedEntityShape(
        bool IsAudited,
        IReadOnlyList<AuditedProperty> Properties,
        IReadOnlyList<IProperty> KeyProperties,
        IProperty? IsDeletedProperty)
    {
        internal static readonly AuditedEntityShape NotAudited = new(false, [], [], null);
    }
}
