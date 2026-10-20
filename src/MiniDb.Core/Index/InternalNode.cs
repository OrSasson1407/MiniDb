using System.Buffers.Binary;
using MiniDb.Records;
using MiniDb.Storage;

namespace MiniDb.Index;

/// <summary>
/// Layout of an internal page: header (type 1, key count 2), first child (8) at offset 11,
/// then (key 8, child 8) pairs from offset 19. Key i is the smallest key under child i+1.
/// </summary>
internal static class InternalNode
{
    private const int CountOffset = 1;
    private const int FirstChildOffset = 11;
    private const int FirstPairOffset = 19;
    private const int PairSize = 16;

    public const int Capacity = (PageConstants.PageSize - FirstPairOffset) / PairSize;

    public static void Format(Span<byte> page, long firstChild)
    {
        page.Clear();
        page[0] = (byte)PageType.Internal;
        BinaryPrimitives.WriteInt64LittleEndian(page[FirstChildOffset..], firstChild);
    }

    public static bool IsInternal(ReadOnlySpan<byte> page) => page[0] == (byte)PageType.Internal;

    public static int KeyCount(ReadOnlySpan<byte> page) => BinaryPrimitives.ReadUInt16LittleEndian(page[CountOffset..]);

    public static long KeyAt(ReadOnlySpan<byte> page, int index) =>
        BinaryPrimitives.ReadInt64LittleEndian(page[PairOffset(index)..]);

    public static long ChildAt(ReadOnlySpan<byte> page, int index) => index == 0
        ? BinaryPrimitives.ReadInt64LittleEndian(page[FirstChildOffset..])
        : BinaryPrimitives.ReadInt64LittleEndian(page[(PairOffset(index - 1) + sizeof(long))..]);

    /// <summary>Index of the child that may contain <paramref name="key"/>: the number of keys less than or equal to it.</summary>
    public static int ChildIndexFor(ReadOnlySpan<byte> page, long key)
    {
        int low = 0;
        int high = KeyCount(page);
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (KeyAt(page, mid) <= key)
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

    /// <summary>Inserts a separator key at <paramref name="index"/> with its right-hand child. The node must not be full.</summary>
    public static void InsertAt(Span<byte> page, int index, long key, long rightChild)
    {
        int count = KeyCount(page);
        int at = PairOffset(index);
        page.Slice(at, (count - index) * PairSize).CopyTo(page[(at + PairSize)..]);
        BinaryPrimitives.WriteInt64LittleEndian(page[at..], key);
        BinaryPrimitives.WriteInt64LittleEndian(page[(at + sizeof(long))..], rightChild);
        SetKeyCount(page, count + 1);
    }

    public static (List<long> Keys, List<long> Children) ReadAll(ReadOnlySpan<byte> page)
    {
        int count = KeyCount(page);
        var keys = new List<long>(count);
        var children = new List<long>(count + 1) { ChildAt(page, 0) };
        for (int i = 0; i < count; i++)
        {
            keys.Add(KeyAt(page, i));
            children.Add(ChildAt(page, i + 1));
        }

        return (keys, children);
    }

    /// <summary>Rewrites the whole page. <paramref name="children"/> must have exactly one more element than <paramref name="keys"/>.</summary>
    public static void WriteAll(Span<byte> page, IReadOnlyList<long> keys, IReadOnlyList<long> children)
    {
        Format(page, children[0]);
        for (int i = 0; i < keys.Count; i++)
        {
            int at = PairOffset(i);
            BinaryPrimitives.WriteInt64LittleEndian(page[at..], keys[i]);
            BinaryPrimitives.WriteInt64LittleEndian(page[(at + sizeof(long))..], children[i + 1]);
        }

        SetKeyCount(page, keys.Count);
    }

    private static int PairOffset(int index) => FirstPairOffset + index * PairSize;

    private static void SetKeyCount(Span<byte> page, int count) =>
        BinaryPrimitives.WriteUInt16LittleEndian(page[CountOffset..], (ushort)count);
}
