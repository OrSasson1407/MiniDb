using MiniDb.Storage;

namespace MiniDb.Index;

/// <summary>Insert and split logic for internal nodes, shared by every tree in the engine.</summary>
internal static class InternalTree
{
    /// <summary>Adds a child's separator to an internal node, splitting the node if it is full.</summary>
    public static TreeSplit? InsertSeparator(IBufferPool pool, PinnedPage page, int childIndex, TreeSplit child) =>
        InsertSeparators(pool, page, childIndex, new[] { child });

    /// <summary>
    /// Adds the separators of several new children, in key order, starting at <paramref name="childIndex"/>.
    /// Splits the node if it cannot hold them all; the node's own separator goes up to the parent.
    /// </summary>
    public static TreeSplit? InsertSeparators(IBufferPool pool, PinnedPage page, int childIndex, IReadOnlyList<TreeSplit> children)
    {
        Span<byte> data = page.Data;
        if (InternalNode.KeyCount(data) + children.Count <= InternalNode.Capacity)
        {
            for (int i = 0; i < children.Count; i++)
            {
                InternalNode.InsertAt(data, childIndex + i, children[i].Key, children[i].PageId);
            }

            page.MarkDirty();
            return null;
        }

        return SplitInternal(pool, page, childIndex, children);
    }

    /// <summary>Creates a new root above an old root that split. Returns the new root's page id.</summary>
    public static long GrowRoot(IBufferPool pool, long oldRoot, TreeSplit split) => GrowRoot(pool, oldRoot, new[] { split });

    /// <summary>Creates a new root above an old root that split into several pages. Returns the new root's page id.</summary>
    public static long GrowRoot(IBufferPool pool, long oldRoot, IReadOnlyList<TreeSplit> splits)
    {
        long newRoot = pool.AllocatePage();
        using PinnedPage page = pool.Pin(newRoot);
        InternalNode.Format(page.Data, oldRoot);
        for (int i = 0; i < splits.Count; i++)
        {
            InternalNode.InsertAt(page.Data, i, splits[i].Key, splits[i].PageId);
        }

        page.MarkDirty();
        return newRoot;
    }

    private static TreeSplit SplitInternal(IBufferPool pool, PinnedPage page, int childIndex, IReadOnlyList<TreeSplit> children)
    {
        (List<TreeKey> keys, List<long> pageIds) = InternalNode.ReadAll(page.Data);
        for (int i = 0; i < children.Count; i++)
        {
            keys.Insert(childIndex + i, children[i].Key);
            pageIds.Insert(childIndex + 1 + i, children[i].PageId);
        }

        int middle = keys.Count / 2;
        TreeKey promoted = keys[middle];
        long newPageId = pool.AllocatePage();
        using (PinnedPage right = pool.Pin(newPageId))
        {
            InternalNode.WriteAll(
                right.Data,
                keys.GetRange(middle + 1, keys.Count - middle - 1),
                pageIds.GetRange(middle + 1, pageIds.Count - middle - 1));
            right.MarkDirty();
        }

        InternalNode.WriteAll(page.Data, keys.GetRange(0, middle), pageIds.GetRange(0, middle + 1));
        page.MarkDirty();
        return new TreeSplit(promoted, newPageId);
    }
}
