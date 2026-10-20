namespace MiniDb.Storage;

/// <summary>
/// A page held in the buffer pool. The page cannot be evicted until this value is disposed, exactly once.
/// It is a struct so that pinning a page does not allocate.
/// </summary>
public readonly struct PinnedPage : IDisposable
{
    private readonly BufferPool _pool;
    private readonly PageFrame _frame;

    internal PinnedPage(BufferPool pool, PageFrame frame)
    {
        _pool = pool;
        _frame = frame;
    }

    public long PageId => _frame.PageId;

    /// <summary>The page bytes. Valid only until the page is disposed.</summary>
    public Span<byte> Data => _frame.Data;

    /// <summary>Marks the page as changed, so it is written by the next FlushAll.</summary>
    public void MarkDirty() => _pool.MarkDirty(_frame);

    public void Dispose() => _pool.Unpin(_frame);
}
