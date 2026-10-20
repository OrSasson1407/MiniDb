using System.Buffers.Binary;
using MiniDb.Records;
using MiniDb.Storage;

namespace MiniDb.Index;

/// <summary>
/// Layout of an internal page: header (type 1, key count 2), first child (8) at offset 11,
/// then (key major 8, key minor 8, child 8) triples from offset 19. Key i is the smallest key under child i+1.
/// </summary>
internal static class InternalNode
{
    private const int CountOffset = 1;
    private const int FirstChildOffset = 11;
    private const int FirstPairOffset = 19;
    private const int PairSize = 24;

    public const int Capacity = (PageConstants.PageSize - FirstPairOffset) / PairSize;

    public static void Format(Span<byte> page, long firstChild)
    {
        page.Clear();
        page[0] = (byte)PageType.Internal;
        BinaryPrimitives.WriteInt64LittleEndian(page[FirstChildOffset..], firstChild);
    }

    public static bool IsInternal(ReadOnlySpan<byte> page) => page[0] == (byte)PageType.Internal;

    public static int KeyCount(ReadOnlySpan<byte> page) => BinaryPrimitives.ReadUInt16LittleEndian(page[CountOffset..]);

    public static TreeKey KeyAt(ReadOnlySpan<byte> page, int index)
    {
        int at = PairOffset(index);
        return new TreeKey(
            BinaryPrimitives.ReadInt64LittleEndian(page[at..]),
            BinaryPrimitives.ReadInt64LittleEndian(page[(at + sizeof(long))..]));
    }

    public static long ChildAt(ReadOnlySpan<byte> page, int index) => index == 0
        ? BinaryPrimitives.ReadInt64LittleEndian(page[FirstChildOffset..])
        : BinaryPrimitives.ReadInt64LittleEndian(page[(PairOffset(index - 1) + 2 * sizeof(long))..]);

    /// <summary>Index of the child that may contain <paramref name="key"/>: the number of keys less than or equal to it.</summary>
    public static int ChildIndexFor(ReadOnlySpan<byte> page, TreeKey key)
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
    public static void InsertAt(Span<byte> page, int index, TreeKey key, long rightChild)
    {
        int count = KeyCount(page);
        int at = PairOffset(index);
        page.Slice(at, (count - index) * PairSize).CopyTo(page[(at + PairSize)..]);
        WriteKeyAndChild(page, at, key, rightChild);
        SetKeyCount(page, count + 1);
    }

    public static (List<TreeKey> Keys, List<long> Children) ReadAll(ReadOnlySpan<byte> page)
    {
        int count = KeyCount(page);
        var keys = new List<TreeKey>(count);
        var children = new List<long>(count + 1) { ChildAt(page, 0) };
        for (int i = 0; i < count; i++)
        {
            keys.Add(KeyAt(page, i));
            children.Add(ChildAt(page, i + 1));
        }

        return (keys, children);
    }

    /// <summary>Rewrites the whole page. <paramref name="children"/> must have exactly one more element than <paramref name="keys"/>.</summary>
    public static void WriteAll(Span<byte> page, IReadOnlyList<TreeKey> keys, IReadOnlyList<long> children)
    {
        Format(page, children[0]);
        for (int i = 0; i < keys.Count; i++)
        {
            WriteKeyAndChild(page, PairOffset(i), keys[i], children[i + 1]);
        }

        SetKeyCount(page, keys.Count);
    }

    private static int PairOffset(int index) => FirstPairOffset + index * PairSize;

    private static void WriteKeyAndChild(Span<byte> page, int at, TreeKey key, long child)
    {
        BinaryPrimitives.WriteInt64LittleEndian(page[at..], key.Major);
        BinaryPrimitives.WriteInt64LittleEndian(page[(at + sizeof(long))..], key.Minor);
        BinaryPrimitives.WriteInt64LittleEndian(page[(at + 2 * sizeof(long))..], child);
    }

    private static void SetKeyCount(Span<byte> page, int count) =>
        BinaryPrimitives.WriteUInt16LittleEndian(page[CountOffset..], (ushort)count);
}
