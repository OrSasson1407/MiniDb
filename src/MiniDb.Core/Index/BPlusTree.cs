using MiniDb.Storage;

namespace MiniDb.Index;

/// <summary>
/// B+ tree from Int64 keys to <see cref="RowLocation"/>. Nodes are pages from the buffer pool.
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

    public RowLocation? Find(long key)
    {
        long leafId = FindLeaf(key);
        using PinnedPage page = _pool.Pin(leafId);
        int index = LeafNode.LowerBound(page.Data, key);
        bool found = index < LeafNode.Count(page.Data) && LeafNode.KeyAt(page.Data, index) == key;
        return found ? LeafNode.LocationAt(page.Data, index) : null;
    }

    /// <summary>Adds a key. Throws <see cref="DuplicateKeyException"/> if the key already exists.</summary>
    public void Insert(long key, RowLocation location)
    {
        Split? split = InsertInto(RootPage, key, location);
        if (split is not { } promoted)
        {
            return;
        }

        long newRoot = _pool.AllocatePage();
        using PinnedPage page = _pool.Pin(newRoot);
        InternalNode.Format(page.Data, RootPage);
        InternalNode.InsertAt(page.Data, 0, promoted.Key, promoted.PageId);
        page.MarkDirty();
        RootPage = newRoot;
    }

    /// <summary>Removes a key. Returns false if it was not present.</summary>
    public bool Delete(long key)
    {
        long leafId = FindLeaf(key);
        using PinnedPage page = _pool.Pin(leafId);
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
    public IEnumerable<(long Key, RowLocation Location)> Range(long from, long to)
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

    private readonly record struct Split(long Key, long PageId);

    private long FindLeaf(long key)
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

    private (List<LeafNode.Entry> Entries, long Next) ReadLeaf(long leafId)
    {
        using PinnedPage page = _pool.Pin(leafId);
        return (LeafNode.ReadAll(page.Data), LeafNode.Next(page.Data));
    }

    private Split? InsertInto(long pageId, long key, RowLocation location)
    {
        using PinnedPage page = _pool.Pin(pageId);
        if (LeafNode.IsLeaf(page.Data))
        {
            return InsertIntoLeaf(page, key, location);
        }

        int childIndex = InternalNode.ChildIndexFor(page.Data, key);
        Split? childSplit = InsertInto(InternalNode.ChildAt(page.Data, childIndex), key, location);
        return childSplit is { } split ? InsertSeparator(page, childIndex, split) : null;
    }

    private Split? InsertIntoLeaf(PinnedPage page, long key, RowLocation location)
    {
        Span<byte> data = page.Data;
        int index = LeafNode.LowerBound(data, key);
        if (index < LeafNode.Count(data) && LeafNode.KeyAt(data, index) == key)
        {
            throw new DuplicateKeyException(key);
        }

        if (LeafNode.Count(data) < LeafNode.Capacity)
        {
            LeafNode.InsertAt(data, index, key, location);
            page.MarkDirty();
            return null;
        }

        return SplitLeaf(page, key, location);
    }

    private Split SplitLeaf(PinnedPage page, long key, RowLocation location)
    {
        List<LeafNode.Entry> entries = LeafNode.ReadAll(page.Data);
        entries.Insert(entries.Count(e => e.Key < key), new LeafNode.Entry(key, location));

        long oldNext = LeafNode.Next(page.Data);
        int half = entries.Count / 2;
        long newPageId = _pool.AllocatePage();
        using (PinnedPage right = _pool.Pin(newPageId))
        {
            LeafNode.WriteAll(right.Data, entries.GetRange(half, entries.Count - half), oldNext);
            right.MarkDirty();
        }

        LeafNode.WriteAll(page.Data, entries.GetRange(0, half), newPageId);
        page.MarkDirty();
        return new Split(entries[half].Key, newPageId);
    }

    private Split? InsertSeparator(PinnedPage page, int childIndex, Split child)
    {
        Span<byte> data = page.Data;
        if (InternalNode.KeyCount(data) < InternalNode.Capacity)
        {
            InternalNode.InsertAt(data, childIndex, child.Key, child.PageId);
            page.MarkDirty();
            return null;
        }

        return SplitInternal(page, childIndex, child);
    }

    private Split SplitInternal(PinnedPage page, int childIndex, Split child)
    {
        (List<long> keys, List<long> children) = InternalNode.ReadAll(page.Data);
        keys.Insert(childIndex, child.Key);
        children.Insert(childIndex + 1, child.PageId);

        int middle = keys.Count / 2;
        long promoted = keys[middle];
        long newPageId = _pool.AllocatePage();
        using (PinnedPage right = _pool.Pin(newPageId))
        {
            InternalNode.WriteAll(
                right.Data,
                keys.GetRange(middle + 1, keys.Count - middle - 1),
                children.GetRange(middle + 1, children.Count - middle - 1));
            right.MarkDirty();
        }

        InternalNode.WriteAll(page.Data, keys.GetRange(0, middle), children.GetRange(0, middle + 1));
        page.MarkDirty();
        return new Split(promoted, newPageId);
    }
}
