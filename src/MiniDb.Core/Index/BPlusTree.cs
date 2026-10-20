using MiniDb.Storage;

namespace MiniDb.Index;

/// <summary>
/// B+ tree from <see cref="TreeKey"/> to <see cref="RowLocation"/>. Nodes are pages from the buffer pool.
/// Inserts split full nodes on the way back up; deletes remove the entry without rebalancing (v1).
/// When the root splits, <see cref="RootPage"/> changes and the owner must persist the new value.
/// </summary>
public sealed class BPlusTree
{
    private readonly IBufferPool _pool;

    private BPlusTree(IBufferPool pool, long rootPage)
    {
        _pool = pool;
        RootPage = rootPage;
    }

    /// <summary>Current root page. Changes when the root splits.</summary>
    public long RootPage { get; private set; }

    /// <summary>Creates an empty tree in newly allocated pages.</summary>
    public static BPlusTree Create(IBufferPool pool)
    {
        long root = pool.AllocatePage();
        using PinnedPage page = pool.Pin(root);
        LeafNode.Format(page.Data, next: 0);
        page.MarkDirty();
        return new BPlusTree(pool, root);
    }

    public static BPlusTree Open(IBufferPool pool, long rootPage) => new(pool, rootPage);

    public RowLocation? Find(TreeKey key)
    {
        using PinnedPage page = FindLeafPinned(key);
        int index = LeafNode.LowerBound(page.Data, key);
        bool found = index < LeafNode.Count(page.Data) && LeafNode.KeyAt(page.Data, index) == key;
        return found ? LeafNode.LocationAt(page.Data, index) : null;
    }

    /// <summary>Adds a key. Throws <see cref="DuplicateKeyException"/> if the key already exists.</summary>
    public void Insert(TreeKey key, RowLocation location)
    {
        TreeSplit? split = InsertInto(RootPage, key, location);
        if (split is not { } promoted)
        {
            return;
        }

        RootPage = InternalTree.GrowRoot(_pool, RootPage, promoted);
    }

    /// <summary>Removes a key. Returns false if it was not present.</summary>
    public bool Delete(TreeKey key)
    {
        using PinnedPage page = FindLeafPinned(key);
        int index = LeafNode.LowerBound(page.Data, key);
        if (index >= LeafNode.Count(page.Data) || LeafNode.KeyAt(page.Data, index) != key)
        {
            return false;
        }

        LeafNode.RemoveAt(page.Data, index);
        page.MarkDirty();
        return true;
    }

    /// <summary>Entries with <paramref name="from"/> &lt;= key &lt;= <paramref name="to"/>, in key order.</summary>
    public IEnumerable<(TreeKey Key, RowLocation Location)> Range(TreeKey from, TreeKey to)
    {
        if (from > to)
        {
            yield break;
        }

        long leafId = FindLeaf(from);
        while (leafId != 0)
        {
            (List<LeafNode.Entry> entries, long next) = ReadLeaf(leafId);
            foreach (LeafNode.Entry entry in entries)
            {
                if (entry.Key < from)
                {
                    continue;
                }

                if (entry.Key > to)
                {
                    yield break;
                }

                yield return (entry.Key, entry.Location);
            }

            leafId = next;
        }
    }

    private long FindLeaf(TreeKey key)
    {
        long pageId = RootPage;
        while (true)
        {
            using PinnedPage page = _pool.Pin(pageId);
            if (LeafNode.IsLeaf(page.Data))
            {
                return pageId;
            }

            pageId = InternalNode.ChildAt(page.Data, InternalNode.ChildIndexFor(page.Data, key));
        }
    }

    /// <summary>Descends to the leaf for <paramref name="key"/> and returns it still pinned. Internal nodes are released on the way down.</summary>
    private PinnedPage FindLeafPinned(TreeKey key)
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

    private (List<LeafNode.Entry> Entries, long Next) ReadLeaf(long leafId)
    {
        using PinnedPage page = _pool.Pin(leafId);
        return (LeafNode.ReadAll(page.Data), LeafNode.Next(page.Data));
    }

    private TreeSplit? InsertInto(long pageId, TreeKey key, RowLocation location)
    {
        using PinnedPage page = _pool.Pin(pageId);
        if (LeafNode.IsLeaf(page.Data))
        {
            return InsertIntoLeaf(page, key, location);
        }

        int childIndex = InternalNode.ChildIndexFor(page.Data, key);
        TreeSplit? childSplit = InsertInto(InternalNode.ChildAt(page.Data, childIndex), key, location);
        return childSplit is { } split ? InternalTree.InsertSeparator(_pool, page, childIndex, split) : null;
    }

    private TreeSplit? InsertIntoLeaf(PinnedPage page, TreeKey key, RowLocation location)
    {
        Span<byte> data = page.Data;
        int index = LeafNode.LowerBound(data, key);
        if (index < LeafNode.Count(data) && LeafNode.KeyAt(data, index) == key)
        {
            throw new DuplicateKeyException(key.Major);
        }

        if (LeafNode.Count(data) < LeafNode.Capacity)
        {
            LeafNode.InsertAt(data, index, key, location);
            page.MarkDirty();
            return null;
        }

        return SplitLeaf(page, key, location);
    }

    private TreeSplit SplitLeaf(PinnedPage page, TreeKey key, RowLocation location)
    {
        List<LeafNode.Entry> entries = LeafNode.ReadAll(page.Data);
        int index = entries.Count(e => e.Key < key);
        entries.Insert(index, new LeafNode.Entry(key, location));

        long oldNext = LeafNode.Next(page.Data);
        // Appending to the rightmost leaf (ascending keys): keep the left leaf full rather than half-empty.
        bool appendingToRightmost = oldNext == 0 && index == entries.Count - 1;
        int half = appendingToRightmost ? entries.Count - 1 : entries.Count / 2;
        long newPageId = _pool.AllocatePage();
        using (PinnedPage right = _pool.Pin(newPageId))
        {
            LeafNode.WriteAll(right.Data, entries.GetRange(half, entries.Count - half), oldNext);
            right.MarkDirty();
        }

        LeafNode.WriteAll(page.Data, entries.GetRange(0, half), newPageId);
        page.MarkDirty();
        return new TreeSplit(entries[half].Key, newPageId);
    }
}
