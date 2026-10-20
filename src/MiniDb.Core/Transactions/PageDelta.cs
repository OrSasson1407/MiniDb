using System.Buffers.Binary;
using MiniDb.Storage;

namespace MiniDb.Transactions;

/// <summary>
/// Describes the bytes that changed in a page as a list of segments: offset (UInt16), length (UInt16), bytes.
/// Only the usable area is compared; the checksum trailer is recomputed when a delta is applied.
/// </summary>
public static class PageDelta
{
    /// <summary>Unchanged gaps this short are bridged, so one segment costs less than two headers.</summary>
    private const int MergeGap = 8;
    private const int SegmentHeader = 4;

    /// <summary>
    /// Writes the delta from <paramref name="before"/> to <paramref name="after"/> into <paramref name="dest"/>.
    /// Returns the number of bytes written, or -1 when the delta would be no smaller than a full page image.
    /// </summary>
    public static int TryCompute(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, Span<byte> dest)
    {
        int usable = PageConstants.UsableSize;
        int written = 0;
        int i = 0;
        while (i < usable)
        {
            if (before[i] == after[i])
            {
                i++;
                continue;
            }

            int start = i;
            int end = i + 1;
            for (int scan = end; scan < usable && scan - end <= MergeGap; scan++)
            {
                if (before[scan] != after[scan])
                {
                    end = scan + 1;
                }
            }

            int length = end - start;
            if (written + SegmentHeader + length >= PageConstants.PageSize)
            {
                return -1;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(dest[written..], (ushort)start);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[(written + 2)..], (ushort)length);
            after.Slice(start, length).CopyTo(dest[(written + SegmentHeader)..]);
            written += SegmentHeader + length;
            i = end;
        }

        return written;
    }

    /// <summary>Writes every segment of <paramref name="delta"/> into <paramref name="page"/>.</summary>
    public static void Apply(Span<byte> page, ReadOnlySpan<byte> delta)
    {
        int at = 0;
        while (at < delta.Length)
        {
            if (at + SegmentHeader > delta.Length)
            {
                throw new InvalidDataException("Truncated page delta.");
            }

            int offset = BinaryPrimitives.ReadUInt16LittleEndian(delta[at..]);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(delta[(at + 2)..]);
            if (at + SegmentHeader + length > delta.Length || offset + length > PageConstants.UsableSize)
            {
                throw new InvalidDataException("Malformed page delta.");
            }

            delta.Slice(at + SegmentHeader, length).CopyTo(page[offset..]);
            at += SegmentHeader + length;
        }
    }
}
