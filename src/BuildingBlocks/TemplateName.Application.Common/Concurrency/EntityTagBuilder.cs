using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;

namespace TemplateName.Application.Common.Concurrency;

/// <summary>
/// Writes a resource's editable values in a canonical form and hashes it into an <see cref="EntityTag"/>. The form starts with the format
/// version (<c>v1</c>) and the resource kind; each value is a length-prefixed token (<c>s{length}:{text};</c>, or <c>n;</c> for null), so no
/// two different sequences of values give the same text; a set is <c>l{count}:</c> and its items in ordinal order, so their order does not
/// matter. The tag is the first 16 bytes of the SHA-256 of the form's UTF-8 (a form holding a lone surrogate is hashed as 0xFF and its
/// UTF-16 code units instead, as UTF-8 cannot carry it), in base64url without padding. Changing the form means a new
/// format version: old tags then stop matching, which answers 412 and makes the client read again.
/// </summary>
public sealed class EntityTagBuilder
{
    private const string FormatVersion = "v1";
    private const int TagBytes = 16;

    private readonly StringBuilder _text = new();

    /// <param name="kind">The resource kind (<c>role</c>, <c>profile</c>, ...), so equal values of two kinds give different tags.</param>
    public EntityTagBuilder(string kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        _text.Append(FormatVersion).Append(';');
        AppendString(kind);
    }

    /// <summary>The canonical form written so far, the input of <see cref="Build"/>.</summary>
    public string CanonicalText => _text.ToString();

    /// <summary>Adds a string as stored (no trimming or case change); null is written distinctly from an empty string.</summary>
    public EntityTagBuilder Add(string? value)
    {
        if (value is null)
        {
            _text.Append("n;");
        }
        else
        {
            AppendString(value);
        }

        return this;
    }

    /// <summary>Adds a Guid as its lower-case <c>D</c> string.</summary>
    public EntityTagBuilder Add(Guid value) => Add(value.ToString("D"));

    /// <summary>Adds <c>true</c> or <c>false</c>.</summary>
    public EntityTagBuilder Add(bool value) => Add(value ? "true" : "false");

    /// <summary>Adds a time as <c>HH:mm:ss.fffffff</c> (invariant), or null.</summary>
    public EntityTagBuilder Add(TimeOnly? value) => Add(value?.ToString("HH':'mm':'ss'.'fffffff", CultureInfo.InvariantCulture));

    /// <summary>Adds a set of strings: its count, then its items in ordinal order.</summary>
    public EntityTagBuilder AddSet(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sorted = values.Order(StringComparer.Ordinal).ToList();
        _text.Append('l').Append(sorted.Count.ToString(CultureInfo.InvariantCulture)).Append(':');
        foreach (var value in sorted)
        {
            AppendString(value);
        }

        return this;
    }

    /// <summary>Adds a set of Guids by their lower-case <c>D</c> strings.</summary>
    public EntityTagBuilder AddSet(IEnumerable<Guid> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return AddSet(values.Select(value => value.ToString("D")));
    }

    /// <summary>Hashes the canonical form into the tag.</summary>
    public EntityTag Build()
    {
        var hash = SHA256.HashData(Encode(CanonicalText));
        return new EntityTag(Base64Url.EncodeToString(hash.AsSpan(0, TagBytes)));
    }

    // UTF-8 for well-formed text. UTF-8 cannot carry a lone surrogate (an encoder replaces it with U+FFFD, so two different values would
    // share a tag), so text that has one is hashed as its UTF-16 code units, little-endian, after a 0xFF byte that UTF-8 never contains.
    private static byte[] Encode(string text)
    {
        var utf8 = new byte[Encoding.UTF8.GetMaxByteCount(text.Length)];
        if (Utf8.FromUtf16(text, utf8, out _, out var written, replaceInvalidSequences: false) == OperationStatus.Done)
        {
            return utf8[..written];
        }

        var utf16 = new byte[1 + (2 * text.Length)];
        utf16[0] = 0xFF;
        for (var index = 0; index < text.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(utf16.AsSpan(1 + (2 * index)), text[index]);
        }

        return utf16;
    }

    private void AppendString(string value)
        => _text.Append('s').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
}
