using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TemplateName.Modules.Auth.Domain.Mfa;

/// <summary>
/// Time-based one-time passwords (RFC 6238 over the HOTP of RFC 4226) with HMAC-SHA-1, <see cref="Digits"/> digits and a
/// <see cref="StepSeconds"/>-second time step counted from the Unix epoch: the parameters every authenticator app supports
/// (ADR 0022). The secret stays in bytes; no method puts it in a <see cref="string"/>.
/// </summary>
internal static class Totp
{
    /// <summary>The number of digits of a code.</summary>
    public const int Digits = 6;

    /// <summary>The length of a time step in seconds.</summary>
    public const int StepSeconds = 30;

    /// <summary>The length of a secret in bytes (160 bits, the HMAC-SHA-1 output size RFC 4226 recommends).</summary>
    public const int SecretLength = 20;

    private const int MinDigits = 6;

    private const int MaxDigits = 8;

    /// <summary>The steps tried: one before the current one, the current one and one after it.</summary>
    private const int CandidateCount = 3;

    /// <summary>A new secret of <see cref="SecretLength"/> bytes from the cryptographic random number generator.</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretLength);

    /// <summary>The time step of <paramref name="instant"/>: <c>floor(unixSeconds / StepSeconds)</c>, whatever its offset.</summary>
    public static long TimeStepAt(DateTimeOffset instant)
    {
        var unixSeconds = instant.ToUnixTimeSeconds();
        var timeStep = unixSeconds / StepSeconds;

        // Integer division rounds toward zero; floor it for instants before the epoch.
        return unixSeconds % StepSeconds < 0 ? timeStep - 1 : timeStep;
    }

    /// <summary>The code of <paramref name="secret"/> at <paramref name="timeStep"/>: RFC 4226 dynamic truncation, zero-padded.</summary>
    /// <exception cref="ArgumentException"><paramref name="secret"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="digits"/> is not 6, 7 or 8 (RFC 4226 Section 5.3).</exception>
    public static string ComputeCode(ReadOnlySpan<byte> secret, long timeStep, int digits = Digits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(digits, MinDigits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(digits, MaxDigits);

        Span<byte> code = stackalloc byte[digits];
        WriteCode(secret, timeStep, code);

        return Encoding.ASCII.GetString(code);
    }

    /// <summary>
    /// The time step among <c>t - 1</c>, <c>t</c> and <c>t + 1</c> (<c>t</c> = <see cref="TimeStepAt"/> of <paramref name="now"/>)
    /// whose code is <paramref name="code"/> and which is greater than <paramref name="lastUsedTimeStep"/>; the highest such step,
    /// or null when there is none or <paramref name="code"/> is not exactly <see cref="Digits"/> ASCII digits.
    /// </summary>
    /// <remarks>
    /// Constant time: every candidate code is computed and compared with
    /// <see cref="CryptographicOperations.FixedTimeEquals"/>, and no result is looked at until all three comparisons are done, so the
    /// response time says nothing about which step (if any) matched or how many digits did. Only the shape of the input (length and
    /// charset, which are not secret) is checked before the comparisons.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="secret"/> is empty (a well-formed code is required to get that far).</exception>
    public static long? FindMatchingStep(ReadOnlySpan<byte> secret, string code, DateTimeOffset now, long? lastUsedTimeStep)
    {
        if (!IsWellFormed(code))
        {
            return null;
        }

        Span<byte> presented = stackalloc byte[Digits];
        for (var i = 0; i < Digits; i++)
        {
            presented[i] = (byte)code[i];
        }

        var firstStep = TimeStepAt(now) - 1;
        Span<byte> candidate = stackalloc byte[Digits];
        Span<bool> isMatch = stackalloc bool[CandidateCount];

        // Phase 1: compute and compare every candidate, with no branch on any comparison result and no early return.
        for (var i = 0; i < CandidateCount; i++)
        {
            WriteCode(secret, firstStep + i, candidate);
            isMatch[i] = CryptographicOperations.FixedTimeEquals(candidate, presented);
        }

        CryptographicOperations.ZeroMemory(candidate);

        // Phase 2: every comparison is done; now pick the highest matching step above the last used one (replay refused).
        long? matchedStep = null;
        for (var i = 0; i < CandidateCount; i++)
        {
            var step = firstStep + i;
            if (isMatch[i] && (lastUsedTimeStep is null || step > lastUsedTimeStep.Value))
            {
                matchedStep = step;
            }
        }

        return matchedStep;
    }

    /// <summary>True when <paramref name="code"/> is exactly <see cref="Digits"/> ASCII digits (full-width digits and spaces are not).</summary>
    private static bool IsWellFormed(string? code)
    {
        if (code is null || code.Length != Digits)
        {
            return false;
        }

        foreach (var character in code)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Writes the code of <paramref name="secret"/> at <paramref name="timeStep"/> as ASCII digits filling <paramref name="destination"/>.</summary>
    private static void WriteCode(ReadOnlySpan<byte> secret, long timeStep, Span<byte> destination)
    {
        if (secret.IsEmpty)
        {
            throw new ArgumentException("The secret is empty.", nameof(secret));
        }

        Span<byte> counter = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counter, timeStep);

        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(secret, counter, hash);

        // RFC 4226 Section 5.3: the low 4 bits of the last byte choose where 31 bits are read.
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        CryptographicOperations.ZeroMemory(hash);

        var value = binary;
        for (var i = destination.Length - 1; i >= 0; i--)
        {
            destination[i] = (byte)('0' + (value % 10));
            value /= 10;
        }
    }
}
