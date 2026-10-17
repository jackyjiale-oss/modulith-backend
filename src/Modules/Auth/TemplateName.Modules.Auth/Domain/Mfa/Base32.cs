namespace TemplateName.Modules.Auth.Domain.Mfa;

/// <summary>
/// Base32 of RFC 4648 Section 6 (alphabet <c>A-Z2-7</c>), the form authenticator apps take a TOTP secret in. <see cref="Encode"/>
/// writes upper case without padding; <see cref="Decode"/> also reads lower case and ignores spaces, so a secret typed by hand
/// in groups works.
/// </summary>
internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private const int BitsPerCharacter = 5;

    /// <summary>Encodes <paramref name="data"/> in upper case without <c>=</c> padding.</summary>
    public static string Encode(ReadOnlySpan<byte> data)
    {
        var length = ((data.Length * 8) + BitsPerCharacter - 1) / BitsPerCharacter;
        var characters = length <= 256 ? stackalloc char[length] : new char[length];

        var buffer = 0;
        var bitCount = 0;
        var position = 0;
        foreach (var value in data)
        {
            buffer = ((buffer << 8) | value) & 0xFFF;
            bitCount += 8;
            while (bitCount >= BitsPerCharacter)
            {
                bitCount -= BitsPerCharacter;
                characters[position++] = Alphabet[(buffer >> bitCount) & 0x1F];
            }
        }

        if (bitCount > 0)
        {
            characters[position] = Alphabet[(buffer << (BitsPerCharacter - bitCount)) & 0x1F];
        }

        return new string(characters);
    }

    /// <summary>Decodes <paramref name="text"/>: case-insensitive, spaces ignored, no padding.</summary>
    /// <exception cref="FormatException">
    /// <paramref name="text"/> contains a character outside the alphabet other than a space (<c>=</c> included), or has a length no
    /// encoding produces (1, 3 or 6 characters modulo 8).
    /// </exception>
    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var characterCount = 0;
        foreach (var character in text)
        {
            if (character == ' ')
            {
                continue;
            }

            if (ValueOf(character) < 0)
            {
                throw new FormatException("The text contains a character outside the base32 alphabet.");
            }

            characterCount++;
        }

        if ((characterCount % 8) is 1 or 3 or 6)
        {
            throw new FormatException("The text has a length no base32 encoding produces.");
        }

        var bytes = new byte[characterCount * BitsPerCharacter / 8];
        var buffer = 0;
        var bitCount = 0;
        var position = 0;
        foreach (var character in text)
        {
            if (character == ' ')
            {
                continue;
            }

            buffer = ((buffer << BitsPerCharacter) | ValueOf(character)) & 0xFFF;
            bitCount += BitsPerCharacter;
            if (bitCount >= 8)
            {
                bitCount -= 8;
                bytes[position++] = (byte)(buffer >> bitCount);
            }
        }

        return bytes;
    }

    /// <summary>The 5-bit value of <paramref name="character"/>, or -1 outside the alphabet (ASCII only, case-insensitive).</summary>
    private static int ValueOf(char character) =>
        character switch
        {
            >= 'A' and <= 'Z' => character - 'A',
            >= 'a' and <= 'z' => character - 'a',
            >= '2' and <= '7' => character - '2' + 26,
            _ => -1,
        };
}
