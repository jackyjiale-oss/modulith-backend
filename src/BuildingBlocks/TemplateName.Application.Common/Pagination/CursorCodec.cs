using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TemplateName.SharedKernel;

namespace TemplateName.Application.Common.Pagination;

/// <summary>
/// Turns a <see cref="Cursor"/> into the opaque token clients send back, and back again. A token is base64url JSON
/// <c>{ "v": 1, "d": direction, "s": sort signature, "f": filter hash, "k": [key values] }</c>. It is not signed (review P6): tampering
/// can only move the caller's position, because every filter is applied server-side and key values are SQL parameters.
/// </summary>
public static class CursorCodec
{
    /// <summary>The longest token <see cref="Decode"/> accepts.</summary>
    public const int MaxTokenLength = 1024;

    private const int Version = 1;
    private const int FilterHashBytes = 16;

    /// <summary>
    /// Encodes <paramref name="cursor"/>. Key values are written in invariant text: <see cref="DateTime"/> (which must be UTC) in the
    /// round-trip format <c>"O"</c>, so no tick is lost, <see cref="DateOnly"/> as <c>yyyy-MM-dd</c>, <see cref="Guid"/> as <c>D</c>.
    /// </summary>
    /// <exception cref="ArgumentException">A <see cref="DateTime"/> key value is not UTC, or a key value has an unsupported type.</exception>
    public static string Encode(Cursor cursor)
    {
        var payload = new Payload(Version, (int)cursor.Direction, cursor.SortSignature, cursor.FilterHash, [.. cursor.KeyValues.Select(Format)]);

        return Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload));
    }

    /// <summary>
    /// Decodes <paramref name="token"/> for the request's <paramref name="sort"/> and <paramref name="filterHash"/>. A malformed or
    /// oversized token, a wrong version, or key values that do not convert to the sort fields' types fail with
    /// <see cref="PaginationErrors.InvalidCursor"/>; a token issued for another sort or other filters fails with
    /// <see cref="PaginationErrors.CursorMismatch"/>.
    /// </summary>
    public static Result<Cursor> Decode(string token, SortSpecification sort, string filterHash)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength || !TryReadPayload(token, out var payload))
        {
            return PaginationErrors.InvalidCursor;
        }

        if (payload.Version != Version
            || !Enum.IsDefined((CursorDirection)payload.Direction)
            || payload.SortSignature is null
            || payload.FilterHash is null
            || payload.KeyValues is null)
        {
            return PaginationErrors.InvalidCursor;
        }

        if (!string.Equals(payload.SortSignature, sort.Signature, StringComparison.Ordinal)
            || !string.Equals(payload.FilterHash, filterHash, StringComparison.Ordinal))
        {
            return PaginationErrors.CursorMismatch;
        }

        if (payload.KeyValues.Length != sort.Terms.Count)
        {
            return PaginationErrors.InvalidCursor;
        }

        var keyValues = new object[payload.KeyValues.Length];
        for (var i = 0; i < keyValues.Length; i++)
        {
            if (payload.KeyValues[i] is not { } text || !TryParse(text, sort.Terms[i].Field.ValueType, out var value))
            {
                return PaginationErrors.InvalidCursor;
            }

            keyValues[i] = value;
        }

        return new Cursor((CursorDirection)payload.Direction, payload.SortSignature, payload.FilterHash, keyValues);
    }

    /// <summary>
    /// Hashes the canonical text of a list request's filters (for example <c>employeeId=…;status=…</c>): the first 16 bytes of its
    /// SHA-256, as lower-case hex. A cursor carries it so that reusing the cursor with other filters is detected.
    /// </summary>
    public static string ComputeFilterHash(string canonicalFilter)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalFilter)).AsSpan(0, FilterHashBytes));

    private static bool TryReadPayload(string token, out Payload payload)
    {
        payload = null!;
        try
        {
            var json = Base64Url.DecodeFromChars(token);
            if (JsonSerializer.Deserialize<Payload>(json) is not { } decoded)
            {
                return false;
            }

            payload = decoded;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return false;
        }
    }

    private static string Format(object value) => value switch
    {
        DateTime { Kind: DateTimeKind.Utc } dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTime => throw new ArgumentException("DateTime key values must be UTC.", nameof(value)),
        DateOnly date => date.ToString("O", CultureInfo.InvariantCulture),
        Guid guid => guid.ToString("D"),
        string text => text,
        int number => number.ToString(CultureInfo.InvariantCulture),
        long number => number.ToString(CultureInfo.InvariantCulture),
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentException($"Key values of type {value.GetType()} are not supported.", nameof(value)),
    };

    private static bool TryParse(string text, Type valueType, out object value)
    {
        var isParsed = false;
        object? parsed = null;

        if (valueType == typeof(DateTime))
        {
            isParsed = DateTime.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTime)
                && dateTime.Kind == DateTimeKind.Utc;
            parsed = dateTime;
        }
        else if (valueType == typeof(DateOnly))
        {
            isParsed = DateOnly.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date);
            parsed = date;
        }
        else if (valueType == typeof(Guid))
        {
            isParsed = Guid.TryParseExact(text, "D", out var guid);
            parsed = guid;
        }
        else if (valueType == typeof(string))
        {
            isParsed = true;
            parsed = text;
        }
        else if (valueType == typeof(int))
        {
            isParsed = int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number);
            parsed = number;
        }
        else if (valueType == typeof(long))
        {
            isParsed = long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number);
            parsed = number;
        }
        else if (valueType == typeof(decimal))
        {
            isParsed = decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number);
            parsed = number;
        }

        value = parsed!;
        return isParsed;
    }

    private sealed record Payload(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("d")] int Direction,
        [property: JsonPropertyName("s")] string? SortSignature,
        [property: JsonPropertyName("f")] string? FilterHash,
        [property: JsonPropertyName("k")] string?[]? KeyValues);
}
