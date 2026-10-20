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

    /// <summary>Changed by the active transaction. Such a page is never evicted or written.</summary>
    public bool TxDirty { get; set; }

    /// <summary>Buffer that holds the page as it was before the active transaction first pinned it. Reused across transactions.</summary>
    public byte[]? SnapshotBuffer { get; set; }

    /// <summary>True while <see cref="SnapshotBuffer"/> holds a valid pre-transaction copy.</summary>
    public bool HasSnapshot { get; set; }

    /// <summary>Whether the page was dirty when <see cref="Snapshot"/> was taken.</summary>
    public bool SnapshotWasDirty { get; set; }

    public int PinCount { get; set; }

    /// <summary>Pins since the page was loaded. Used to update the LRU list only on some of them.</summary>
    public int Hits { get; set; }

    /// <summary>Lock of the partition that owns this frame. Set when the frame is created.</summary>
    public object? Gate { get; set; }

    /// <summary>This frame's entry in the pool's LRU list.</summary>
    public LinkedListNode<PageFrame> Node { get; }
}
