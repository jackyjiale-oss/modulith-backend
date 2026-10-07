using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TemplateName.SharedKernel;

/// <summary>
/// Creates GUIDs that increase with time in SQL Server's <c>uniqueidentifier</c> sort order, which compares bytes 10 to 15 first
/// (most significant first). This keeps clustered-index inserts sequential. <see cref="Guid.CreateVersion7()" /> does not, because it
/// puts the timestamp in the first bytes. See ADR 0004.
/// </summary>
public static class SequentialGuid
{
    private const int TimestampOffset = 10;
    private const int TimestampLength = 6;

    public static Guid Create(DateTimeOffset timestamp)
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes[..TimestampOffset]);

        // The Unix millisecond count as a 48-bit big-endian integer: the low six bytes of a 64-bit big-endian value.
        Span<byte> milliseconds = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(milliseconds, timestamp.ToUnixTimeMilliseconds());
        milliseconds[(sizeof(long) - TimestampLength)..].CopyTo(bytes[TimestampOffset..]);

        return new Guid(bytes);
    }
}
