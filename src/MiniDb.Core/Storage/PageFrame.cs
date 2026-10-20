namespace MiniDb.Storage;

/// <summary>One slot of the buffer pool: a page image plus its bookkeeping. Reused across pages.</summary>
internal sealed class PageFrame
{
    public PageFrame()
    {
        Node = new LinkedListNode<PageFrame>(this);
    }

    public long PageId { get; set; }

    public byte[] Data { get; } = new byte[PageConstants.PageSize];

    public bool IsDirty { get; set; }

    public int PinCount { get; set; }

    /// <summary>This frame's entry in the pool's LRU list.</summary>
    public LinkedListNode<PageFrame> Node { get; }
}
