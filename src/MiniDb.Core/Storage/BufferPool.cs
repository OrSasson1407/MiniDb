namespace MiniDb.Storage;

/// <summary>
/// LRU page cache with a fixed number of frames. A page changed by the active transaction is never evicted or
/// written, so uncommitted data never reaches the data file (no-steal). A page changed by a committed transaction
/// may be written back at any time, because its change is already in the WAL.
/// </summary>
public sealed class BufferPool : IBufferPool
{
    private readonly IPageStore _store;
    private readonly Dictionary<long, PageFrame> _cached = new();
    private readonly LinkedList<PageFrame> _lru = new();
    private readonly List<PageFrame> _frames = new();
    private readonly Stack<PageFrame> _freeFrames = new();
    private readonly HashSet<long> _trackedPages = new();
    private readonly List<PageFrame> _snapshotted = new();
    private bool _tracking;

    public BufferPool(IPageStore store, int capacity = 256)
    {
        _store = store;
        Capacity = capacity;
    }

    public int Capacity { get; }

    /// <summary>Called before any page is written to the data file, so the log is durable first.</summary>
    public Action? BeforeWriteBack { get; set; }

    public PinnedPage Pin(long pageId) => Pin(pageId, sequential: false);

    /// <summary>
    /// Pins a page. A sequential access (a table scan) neither promotes a cached page nor loads it at the hot end,
    /// so one long scan cannot push out the pages that point lookups keep needing.
    /// </summary>
    public PinnedPage Pin(long pageId, bool sequential)
    {
        PageFrame frame;
        if (_cached.TryGetValue(pageId, out PageFrame? cached))
        {
            frame = cached;
            if (!sequential)
            {
                Touch(frame);
            }
        }
        else
        {
            frame = LoadFrame(pageId, sequential);
        }

        frame.PinCount++;
        if (_tracking && !frame.HasSnapshot)
        {
            // Keep the state before the transaction touches the page, so rollback can restore it.
            frame.SnapshotBuffer ??= new byte[PageConstants.PageSize];
            frame.Data.AsSpan().CopyTo(frame.SnapshotBuffer);
            frame.HasSnapshot = true;
            frame.SnapshotWasDirty = frame.IsDirty;
            _snapshotted.Add(frame);
        }

        return new PinnedPage(this, frame);
    }

    public long AllocatePage() => _store.AllocatePage();

    /// <summary>Pages changed by the active transaction.</summary>
    public IReadOnlyCollection<long> TrackedPages => _trackedPages;

    /// <summary>
    /// For a page changed by the active transaction: its image before the transaction and its current image.
    /// Returns false when no before-image was taken.
    /// </summary>
    public bool TryGetBeforeImage(long pageId, out ReadOnlySpan<byte> before, out ReadOnlySpan<byte> current)
    {
        if (_cached.TryGetValue(pageId, out PageFrame? frame) && frame.HasSnapshot)
        {
            before = frame.SnapshotBuffer;
            current = frame.Data;
            return true;
        }

        before = ReadOnlySpan<byte>.Empty;
        current = ReadOnlySpan<byte>.Empty;
        return false;
    }

    /// <summary>Starts recording a transaction. Pages it changes stay in the pool until it commits or rolls back.</summary>
    public void BeginTracking()
    {
        _tracking = true;
        _trackedPages.Clear();
    }

    /// <summary>The transaction committed. Its pages become ordinary dirty pages that may be written back.</summary>
    public void CommitTracking()
    {
        foreach (long pageId in _trackedPages)
        {
            if (_cached.TryGetValue(pageId, out PageFrame? frame))
            {
                frame.TxDirty = false;
            }
        }

        EndTracking();
    }

    /// <summary>The transaction rolled back. Each page it changed goes back to its state before the transaction.</summary>
    public void RollbackTracking()
    {
        foreach (long pageId in _trackedPages)
        {
            if (_cached.TryGetValue(pageId, out PageFrame? frame) && frame.HasSnapshot)
            {
                frame.SnapshotBuffer!.AsSpan().CopyTo(frame.Data);
                frame.IsDirty = frame.SnapshotWasDirty;
                frame.TxDirty = false;
            }
        }

        EndTracking();
    }

    /// <summary>Returns a copy of the page's current bytes, loading it if needed.</summary>
    public byte[] ReadCopy(long pageId)
    {
        using PinnedPage page = Pin(pageId);
        return page.Data.ToArray();
    }

    internal void MarkDirty(PageFrame frame)
    {
        frame.IsDirty = true;
        if (_tracking)
        {
            frame.TxDirty = true;
            _trackedPages.Add(frame.PageId);
        }
    }

    /// <summary>Writes every dirty page that is not part of an active transaction.</summary>
    public void FlushAll()
    {
        foreach (PageFrame frame in _cached.Values.Where(f => f.IsDirty && !f.TxDirty))
        {
            WriteBack(frame);
        }
    }

    public void Discard(long pageId)
    {
        if (!_cached.TryGetValue(pageId, out PageFrame? frame))
        {
            return;
        }

        if (frame.PinCount > 0)
        {
            throw new InvalidOperationException($"Page {pageId} is pinned and cannot be discarded.");
        }

        Remove(frame);
        frame.IsDirty = false;
        _freeFrames.Push(frame);
    }

    /// <summary>Intentionally does not flush: the transaction layer decides what reaches disk.</summary>
    public void Dispose()
    {
    }

    internal void Unpin(PageFrame frame)
    {
        if (frame.PinCount > 0)
        {
            frame.PinCount--;
        }
    }

    private void EndTracking()
    {
        foreach (PageFrame frame in _snapshotted)
        {
            frame.HasSnapshot = false;
        }

        _snapshotted.Clear();
        _trackedPages.Clear();
        _tracking = false;
    }

    private void WriteBack(PageFrame frame)
    {
        BeforeWriteBack?.Invoke();
        PageChecksum.Stamp(frame.Data);
        _store.WritePage(frame.PageId, frame.Data);
        frame.IsDirty = false;
    }

    /// <summary>Loads a page without pinning it, so a later sequential Pin finds it in the cache.</summary>
    public void Prefetch(long pageId)
    {
        if (!_cached.ContainsKey(pageId))
        {
            LoadFrame(pageId, sequential: true);
        }
    }

    private PageFrame LoadFrame(long pageId, bool sequential)
    {
        PageFrame frame = TakeFrame();
        try
        {
            _store.ReadPage(pageId, frame.Data);
            PageChecksum.Verify(frame.Data, pageId);
        }
        catch
        {
            _freeFrames.Push(frame);
            throw;
        }

        frame.PageId = pageId;
        frame.IsDirty = false;
        frame.TxDirty = false;
        frame.HasSnapshot = false;
        _cached.Add(pageId, frame);
        if (sequential)
        {
            _lru.AddLast(frame.Node);
        }
        else
        {
            _lru.AddFirst(frame.Node);
        }

        return frame;
    }

    private PageFrame TakeFrame()
    {
        if (_freeFrames.Count > 0)
        {
            return _freeFrames.Pop();
        }

        if (_frames.Count < Capacity)
        {
            var created = new PageFrame();
            _frames.Add(created);
            return created;
        }

        PageFrame victim = FindEvictable() ?? throw new BufferPoolFullException(Capacity);
        if (victim.IsDirty)
        {
            WriteBack(victim);
        }

        Remove(victim);
        return victim;
    }

    private PageFrame? FindEvictable()
    {
        for (LinkedListNode<PageFrame>? node = _lru.Last; node is not null; node = node.Previous)
        {
            if (node.Value.PinCount == 0 && !node.Value.TxDirty)
            {
                return node.Value;
            }
        }

        return null;
    }

    private void Touch(PageFrame frame)
    {
        _lru.Remove(frame.Node);
        _lru.AddFirst(frame.Node);
    }

    private void Remove(PageFrame frame)
    {
        _cached.Remove(frame.PageId);
        _lru.Remove(frame.Node);
        frame.HasSnapshot = false;
        frame.TxDirty = false;
    }
}
