using System.Buffers.Binary;

namespace MiniDb.Storage;

/// <summary>Stamps and verifies the CRC-32 kept in the last bytes of every data page.</summary>
public static class PageChecksum
{
    public static void Stamp(Span<byte> page)
    {
        uint crc = Checksum.Compute(page[..PageConstants.UsableSize]);
        BinaryPrimitives.WriteUInt32LittleEndian(page[PageConstants.UsableSize..], crc);
    }

    /// <summary>Throws <see cref="CorruptPageException"/> on a mismatch. A page that was never written (all zeros) is valid.</summary>
    public static void Verify(ReadOnlySpan<byte> page, long pageId)
    {
        if (page.IndexOfAnyExcept((byte)0) < 0)
        {
            return;
        }

        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(page[PageConstants.UsableSize..]);
        if (Checksum.Compute(page[..PageConstants.UsableSize]) != stored)
        {
            throw new CorruptPageException(pageId);
        }
    }
}
