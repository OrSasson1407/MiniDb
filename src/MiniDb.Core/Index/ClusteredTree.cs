using System.Buffers.Binary;
using MiniDb.Records;
using MiniDb.Storage;

namespace MiniDb.Index;

/// <summary>
/// Primary-key tree that keeps each row inside its leaf. A leaf is a slotted page whose cells are
/// [key 8 bytes][record bytes], kept in key order by the slot directory. Internal nodes are shared with <see cref="BPlusTree"/>.
/// </summary>
public sealed class ClusteredTree
{
    private const int KeyBytes = sizeof(long);
    private readonly IBufferPool _pool;

    private ClusteredTree(IBufferPool pool, long rootPage)
    {
        _pool = pool;
        RootPage = rootPage;
    }

    /// <summary>Current root page. Changes when the root splits.</summary>
    public long RootPage { get; private set; }

    public static ClusteredTree Create(IBufferPool pool)
    {
        long root = pool.AllocatePage();
        using PinnedPage page = pool.Pin(root);
        SlottedPage.Format(page.Data, root, PageType.Leaf);
        page.MarkDirty();
        return new ClusteredTree(pool, root);
    }

    public static ClusteredTree Open(IBufferPool pool, long rootPage) => new(pool, rootPage);

    /// <summary>The record stored under <paramref name="key"/>, or null.</summary>
    public byte[]? Find(long key)
    {
        using PinnedPage page = FindLeafPinned(key);
        SlottedPage leaf = SlottedPage.Open(page.Data);
        int index = LowerBound(leaf, key);
        if (index >= leaf.SlotCount || KeyOf(leaf.Read(index)) != key)
        {
            return null;
        }

        return leaf.Read(index)[KeyBytes..].ToArray();
    }

    /// <summary>Stores a record under a new key. Throws <see cref="DuplicateKeyException"/> if the key exists.</summary>
    public void Insert(long key, byte[] record)
    {
        int cellSize = KeyBytes + record.Length;
        if (cellSize > SlottedPage.MaxRecordSize)
        {
            throw new RecordTooLargeException(cellSize);
        }

        TreeSplit? split = InsertInto(RootPage, key, BuildCell(key, record));
        if (split is { } promoted)
        {
            RootPage = InternalTree.GrowRoot(_pool, RootPage, promoted);
        }
    }

    /// <summary>Removes a key. Returns false if it was not present.</summary>
    public bool Delete(long key)
    {
        using PinnedPage page = FindLeafPinned(key);
        SlottedPage leaf = SlottedPage.Open(page.Data);
        int index = LowerBound(leaf, key);
        if (index >= leaf.SlotCount || KeyOf(leaf.Read(index)) != key)
        {
            return false;
        }

        leaf.RemoveAt(index);
        page.MarkDirty();
        return true;
    }

    /// <summary>Records with <paramref name="from"/> &lt;= key &lt;= <paramref name="to"/>, in key order.</summary>
    public IEnumerable<(long Key, byte[] Record)> Range(long from, long to)
    {
        if (from > to)
        {
            yield break;
        }

        long pageId = FindLeaf(from);
        while (pageId != 0)
        {
            using PinnedPage page = _pool.Pin(pageId);
            int slots = SlotsOf(page.Data);
            for (int i = 0; i < slots; i++)
            {
                long key = KeyAt(page.Data, i);
                if (key < from)
                {
                    continue;
                }

                if (key > to)
                {
                    yield break;
                }

                yield return (key, RecordAt(page.Data, i));
            }

            pageId = NextOf(page.Data);
        }
    }

    private static int SlotsOf(Span<byte> data) => SlottedPage.Open(data).SlotCount;

    private static long NextOf(Span<byte> data) => SlottedPage.Open(data).NextPage;

    private static long KeyAt(Span<byte> data, int index) => KeyOf(SlottedPage.Open(data).Read(index));

    private static byte[] RecordAt(Span<byte> data, int index) => SlottedPage.Open(data).Read(index)[KeyBytes..].ToArray();

    private static byte[] BuildCell(long key, byte[] record)
    {
        var cell = new byte[KeyBytes + record.Length];
        BinaryPrimitives.WriteInt64LittleEndian(cell, key);
        record.CopyTo(cell, KeyBytes);
        return cell;
    }

    private static long KeyOf(ReadOnlySpan<byte> cell) => BinaryPrimitives.ReadInt64LittleEndian(cell);

    private static int LowerBound(SlottedPage leaf, long key)
    {
        int low = 0;
        int high = leaf.SlotCount;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (KeyOf(leaf.Read(mid)) < key)
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

    private TreeSplit? InsertInto(long pageId, long key, byte[] cell)
    {
        using PinnedPage page = _pool.Pin(pageId);
        if (LeafNode.IsLeaf(page.Data))
        {
            return InsertIntoLeaf(page, key, cell);
        }

        int childIndex = InternalNode.ChildIndexFor(page.Data, key);
        TreeSplit? childSplit = InsertInto(InternalNode.ChildAt(page.Data, childIndex), key, cell);
        return childSplit is { } split ? InternalTree.InsertSeparator(_pool, page, childIndex, split) : null;
    }

    private TreeSplit? InsertIntoLeaf(PinnedPage page, long key, byte[] cell)
    {
        SlottedPage leaf = SlottedPage.Open(page.Data);
        int index = LowerBound(leaf, key);
        if (index < leaf.SlotCount && KeyOf(leaf.Read(index)) == key)
        {
            throw new DuplicateKeyException(key);
        }

        if (leaf.InsertAt(index, cell))
        {
            page.MarkDirty();
            return null;
        }

        return SplitLeaf(page, index, cell);
    }

    private TreeSplit SplitLeaf(PinnedPage page, int index, byte[] cell)
    {
        List<byte[]> cells = ReadCells(page.Data);
        cells.Insert(index, cell);

        long oldNext = SlottedPage.Open(page.Data).NextPage;
        // Ascending keys append to the rightmost leaf: keep the left leaf full rather than half-empty.
        bool appendingToRightmost = oldNext == 0 && index == cells.Count - 1;
        int split = ChooseSplit(cells, appendingToRightmost);

        long newPageId = _pool.AllocatePage();
        using (PinnedPage right = _pool.Pin(newPageId))
        {
            Rebuild(right, cells, split, cells.Count - split, oldNext);
        }

        Rebuild(page, cells, 0, split, newPageId);
        return new TreeSplit(KeyOf(cells[split]), newPageId);
    }

    /// <summary>Index of the first cell that goes to the right page. Both halves must fit in one page.</summary>
    private static int ChooseSplit(List<byte[]> cells, bool appendingToRightmost)
    {
        if (appendingToRightmost)
        {
            return cells.Count - 1;
        }

        int capacity = PageConstants.UsableSize - SlottedPage.HeaderSize;
        int total = cells.Sum(c => c.Length + SlottedPage.SlotSize);
        int best = -1;
        int bestBalance = int.MaxValue;
        int leftBytes = 0;
        for (int k = 1; k < cells.Count; k++)
        {
            leftBytes += cells[k - 1].Length + SlottedPage.SlotSize;
            int rightBytes = total - leftBytes;
            if (leftBytes <= capacity && rightBytes <= capacity)
            {
                int balance = Math.Abs(leftBytes - rightBytes);
                if (balance < bestBalance)
                {
                    best = k;
                    bestBalance = balance;
                }
            }
        }

        if (best < 0)
        {
            throw new InvalidOperationException("The cells of a full leaf cannot be divided between two pages.");
        }

        return best;
    }

    private static void Rebuild(PinnedPage target, List<byte[]> cells, int from, int count, long next)
    {
        SlottedPage leaf = SlottedPage.Format(target.Data, target.PageId, PageType.Leaf);
        leaf.NextPage = next;
        for (int i = 0; i < count; i++)
        {
            if (!leaf.InsertAt(leaf.SlotCount, cells[from + i]))
            {
                throw new InvalidOperationException("A split half does not fit in a page.");
            }
        }

        target.MarkDirty();
    }

    private static List<byte[]> ReadCells(Span<byte> data)
    {
        SlottedPage leaf = SlottedPage.Open(data);
        var cells = new List<byte[]>(leaf.SlotCount);
        for (int i = 0; i < leaf.SlotCount; i++)
        {
            cells.Add(leaf.Read(i).ToArray());
        }

        return cells;
    }

    private long FindLeaf(long key)
    {
        using PinnedPage page = FindLeafPinned(key);
        return page.PageId;
    }

    /// <summary>Descends to the leaf for <paramref name="key"/> and returns it still pinned.</summary>
    private PinnedPage FindLeafPinned(long key)
    {
        long pageId = RootPage;
        while (true)
        {
            PinnedPage page = _pool.Pin(pageId);
            if (LeafNode.IsLeaf(page.Data))
            {
                return page;
            }

            long child = InternalNode.ChildAt(page.Data, InternalNode.ChildIndexFor(page.Data, key));
            page.Dispose();
            pageId = child;
        }
    }
}
