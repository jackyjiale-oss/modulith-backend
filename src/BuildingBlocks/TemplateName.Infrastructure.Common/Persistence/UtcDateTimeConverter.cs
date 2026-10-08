using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TemplateName.Infrastructure.Common.Persistence;

/// <summary>
/// Stores <see cref="DateTime"/> values as UTC and reads them back with <see cref="DateTimeKind.Utc"/>. A local value is converted to
/// UTC on write; an unspecified one is taken to be UTC already.
/// </summary>
internal sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc),
    value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
