using System.Security.Cryptography;
using System.Text;

namespace TemplateName.Modules.Auth.Domain.Mfa;

/// <summary>
/// The format of recovery codes: <see cref="Count"/> codes of <see cref="Length"/> characters from <see cref="Alphabet"/> (32 symbols,
/// 5 bits each: 80 bits per code), shown as <c>XXXX-XXXX-XXXX-XXXX</c> and stored only as the SHA-256 of the normalized code. 80 bits
/// make an unsalted fast hash safe against offline guessing (ADR 0022).
/// </summary>
internal static class RecoveryCodeFormat
{
    /// <summary>The number of codes a user gets at once.</summary>
    public const int Count = 10;

    /// <summary>The number of characters of a normalized code.</summary>
    public const int Length = 16;

    /// <summary>Digits and upper-case letters without <c>0</c>, <c>1</c>, <c>I</c> and <c>O</c>, which are easily misread.</summary>
    public const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    private const int GroupLength = 4;

    private const char GroupSeparator = '-';

    /// <summary>A new normalized code of <see cref="Length"/> characters chosen uniformly by the cryptographic random number generator.</summary>
    public static string Generate() => new(RandomNumberGenerator.GetItems(Alphabet.AsSpan(), Length));

    /// <summary>
    /// The normalized form of <paramref name="input"/>: trimmed, ASCII letters upper-cased, <c>-</c> and spaces removed; null unless
    /// exactly <see cref="Length"/> characters of <see cref="Alphabet"/> remain.
    /// </summary>
    /// <remarks>
    /// Only ASCII <c>a</c>-<c>z</c> are upper-cased, by arithmetic rather than a culture's (or the invariant culture's) case
    /// mapping, so a non-ASCII letter whose upper case is an ASCII one (the long s, U+017F) is refused instead of normalized.
    /// </remarks>
    public static string? Normalize(string input)
    {
        if (input is null)
        {
            return null;
        }

        Span<char> normalized = stackalloc char[Length];
        var count = 0;
        foreach (var character in input.AsSpan().Trim())
        {
            if (character is GroupSeparator or ' ')
            {
                continue;
            }

            var upper = character is >= 'a' and <= 'z' ? (char)(character - ('a' - 'A')) : character;
            if (count == Length || !Alphabet.Contains(upper, StringComparison.Ordinal))
            {
                return null;
            }

            normalized[count++] = upper;
        }

        return count == Length ? new string(normalized) : null;
    }

    /// <summary>The display form of a normalized code: groups of four separated by <c>-</c> (<c>XXXX-XXXX-XXXX-XXXX</c>).</summary>
    /// <exception cref="ArgumentException"><paramref name="normalized"/> is not a normalized code.</exception>
    public static string Display(string normalized)
    {
        EnsureNormalized(normalized);

        var builder = new StringBuilder(Length + (Length / GroupLength) - 1);
        for (var i = 0; i < Length; i += GroupLength)
        {
            if (i > 0)
            {
                builder.Append(GroupSeparator);
            }

            builder.Append(normalized, i, GroupLength);
        }

        return builder.ToString();
    }

    /// <summary>The SHA-256 of the UTF-8 bytes of a normalized code: the only form a code is stored in.</summary>
    /// <exception cref="ArgumentException"><paramref name="normalized"/> is not a normalized code.</exception>
    public static byte[] Hash(string normalized)
    {
        EnsureNormalized(normalized);

        return SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
    }

    private static void EnsureNormalized(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        if (!string.Equals(Normalize(normalized), normalized, StringComparison.Ordinal))
        {
            throw new ArgumentException("The value is not a normalized recovery code.", nameof(normalized));
        }
    }
}
