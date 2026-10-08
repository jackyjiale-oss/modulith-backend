using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TemplateName.Infrastructure.Common.Persistence;

/// <summary>
/// Stores a <see cref="DateTimeOffset"/> as its UTC instant in a <see cref="DateTime"/> column and reads it back with offset zero, so
/// <see cref="DateTimeOffset"/> and <see cref="DateTime"/> properties share one column type and Dapper reads both as UTC
/// <see cref="DateTime"/> values. The original offset is not kept.
/// </summary>
internal sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTime>(
    value => value.UtcDateTime,
    value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
