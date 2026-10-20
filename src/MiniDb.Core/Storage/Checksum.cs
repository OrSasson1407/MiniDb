using System.IO.Hashing;

namespace MiniDb.Storage;

/// <summary>CRC-32 used to detect torn or corrupted pages and WAL frames.</summary>
public static class Checksum
{
    public static uint Compute(ReadOnlySpan<byte> data) => Crc32.HashToUInt32(data);
}
