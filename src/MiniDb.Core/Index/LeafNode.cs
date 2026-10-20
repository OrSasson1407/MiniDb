using System.Buffers.Binary;
using MiniDb.Records;
using MiniDb.Storage;

namespace MiniDb.Index;

/// <summary>
/// Layout of a leaf page: header (type 1, count 2, next leaf 8) followed by sorted entries
/// (key 8, page id 8, slot 4). Static helpers work directly on the page bytes.
/// </summary>
internal static class LeafNode
{
    public const int HeaderSize = 11;
    public const int EntrySize = 20;
    public const int Capacity = (PageConstants.PageSize - HeaderSize) / EntrySize;

    private const int CountOffset = 1;
    private const int NextOffset = 3;

    public readonly record struct Entry(long Key, RowLocation Location);

    public static void Format(Span<byte> page, long next)
    {
        page.Clear();
        page[0] = (byte)PageType.Leaf;
        SetNext(page, next);
    }

    public static bool IsLeaf(ReadOnlySpan<byte> page) => page[0] == (byte)PageType.Leaf;

    public static int Count(ReadOnlySpan<byte> page) => BinaryPrimitives.ReadUInt16LittleEndian(page[CountOffset..]);

    public static long Next(ReadOnlySpan<byte> page) => BinaryPrimitives.ReadInt64LittleEndian(page[NextOffset..]);

    public static void SetNext(Span<byte> page, long next) => BinaryPrimitives.WriteInt64LittleEndian(page[NextOffset..], next);

    public static long KeyAt(ReadOnlySpan<byte> page, int index) => BinaryPrimitives.ReadInt64LittleEndian(page[EntryOffset(index)..]);

    public static RowLocation LocationAt(ReadOnlySpan<byte> page, int index)
    {
        int at = EntryOffset(index) + sizeof(long);
        return new RowLocation(
            BinaryPrimitives.ReadInt64LittleEndian(page[at..]),
            BinaryPrimitives.ReadInt32LittleEndian(page[(at + sizeof(long))..]));
    }

    /// <summary>Index of the first entry whose key is greater than or equal to <paramref name="key"/>.</summary>
    public static int LowerBound(ReadOnlySpan<byte> page, long key)
    {
        int low = 0;
        int high = Count(page);
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (KeyAt(page, mid) < key)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    /// <summary>Inserts an entry at <paramref name="index"/>, shifting later entries right. The leaf must not be full.</summary>
    public static void InsertAt(Span<byte> page, int index, long key, RowLocation location)
    {
        int count = Count(page);
        int at = EntryOffset(index);
        page.Slice(at, (count - index) * EntrySize).CopyTo(page[(at + EntrySize)..]);
        WriteEntry(page, index, key, location);
        SetCount(page, count + 1);
    }

    public static void RemoveAt(Span<byte> page, int index)
    {
        int count = Count(page);
        int at = EntryOffset(index);
        page.Slice(at + EntrySize, (count - index - 1) * EntrySize).CopyTo(page[at..]);
        SetCount(page, count - 1);
    }

    public static List<Entry> ReadAll(ReadOnlySpan<byte> page)
    {
        int count = Count(page);
        var entries = new List<Entry>(count);
        for (int i = 0; i < count; i++)
        {
            entries.Add(new Entry(KeyAt(page, i), LocationAt(page, i)));
        }

        return entries;
    }

    /// <summary>Rewrites the whole page with the given entries and next-leaf pointer.</summary>
    public static void WriteAll(Span<byte> page, IReadOnlyList<Entry> entries, long next)
    {
        Format(page, next);
        for (int i = 0; i < entries.Count; i++)
        {
            WriteEntry(page, i, entries[i].Key, entries[i].Location);
        }

        SetCount(page, entries.Count);
    }

    private static int EntryOffset(int index) => HeaderSize + index * EntrySize;

    private static void SetCount(Span<byte> page, int count) =>
        BinaryPrimitives.WriteUInt16LittleEndian(page[CountOffset..], (ushort)count);

    private static void WriteEntry(Span<byte> page, int index, long key, RowLocation location)
    {
        int at = EntryOffset(index);
        BinaryPrimitives.WriteInt64LittleEndian(page[at..], key);
        BinaryPrimitives.WriteInt64LittleEndian(page[(at + sizeof(long))..], location.PageId);
        BinaryPrimitives.WriteInt32LittleEndian(page[(at + 2 * sizeof(long))..], location.Slot);
    }
}
