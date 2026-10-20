using MiniDb.Storage;

namespace MiniDb.Index;

/// <summary>Insert and split logic for internal nodes, shared by every tree in the engine.</summary>
internal static class InternalTree
{
    /// <summary>Adds a child's separator to an internal node, splitting the node if it is full.</summary>
    public static TreeSplit? InsertSeparator(IBufferPool pool, PinnedPage page, int childIndex, TreeSplit child)
    {
        Span<byte> data = page.Data;
        if (InternalNode.KeyCount(data) < InternalNode.Capacity)
        {
            InternalNode.InsertAt(data, childIndex, child.Key, child.PageId);
            page.MarkDirty();
            return null;
        }

        return SplitInternal(pool, page, childIndex, child);
    }

    /// <summary>Creates a new root above an old root that split. Returns the new root's page id.</summary>
    public static long GrowRoot(IBufferPool pool, long oldRoot, TreeSplit split)
    {
        long newRoot = pool.AllocatePage();
        using PinnedPage page = pool.Pin(newRoot);
        InternalNode.Format(page.Data, oldRoot);
        InternalNode.InsertAt(page.Data, 0, split.Key, split.PageId);
        page.MarkDirty();
        return newRoot;
    }

    private static TreeSplit SplitInternal(IBufferPool pool, PinnedPage page, int childIndex, TreeSplit child)
    {
        (List<TreeKey> keys, List<long> children) = InternalNode.ReadAll(page.Data);
        keys.Insert(childIndex, child.Key);
        children.Insert(childIndex + 1, child.PageId);

        int middle = keys.Count / 2;
        TreeKey promoted = keys[middle];
        long newPageId = pool.AllocatePage();
        using (PinnedPage right = pool.Pin(newPageId))
        {
            InternalNode.WriteAll(
                right.Data,
                keys.GetRange(middle + 1, keys.Count - middle - 1),
                children.GetRange(middle + 1, children.Count - middle - 1));
            right.MarkDirty();
        }

        InternalNode.WriteAll(page.Data, keys.GetRange(0, middle), children.GetRange(0, middle + 1));
        page.MarkDirty();
        return new TreeSplit(promoted, newPageId);
    }
}
