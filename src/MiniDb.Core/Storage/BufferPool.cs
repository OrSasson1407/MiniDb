namespace MiniDb.Storage;

/// <summary>
/// LRU page cache with a fixed number of frames. Dirty and pinned pages are never evicted,
/// so a page changed by an uncommitted transaction never reaches the data file on its own (no-steal).
/// </summary>
public sealed class BufferPool : IBufferPool
{
    private readonly IPageStore _store;
    private readonly Dictionary<long, PageFrame> _cached = new();
    private readonly LinkedList<PageFrame> _lru = new();
    private readonly List<PageFrame> _frames = new();
    private readonly Stack<PageFrame> _freeFrames = new();
    private readonly HashSet<long> _trackedPages = new();

    public BufferPool(IPageStore store, int capacity = 256)
    {
        _store = store;
        Capacity = capacity;
    }

    public int Capacity { get; }

    public PinnedPage Pin(long pageId)
    {
        PageFrame frame = _cached.TryGetValue(pageId, out PageFrame? cached) ? cached : LoadFrame(pageId);
        frame.PinCount++;
        Touch(frame);
        return new PinnedPage(this, frame);
    }

    public long AllocatePage() => _store.AllocatePage();

    /// <summary>Pages marked dirty since the last <see cref="ClearTracking"/>. These are the pages a transaction changed.</summary>
    public IReadOnlyCollection<long> TrackedPages => _trackedPages;

    public void ClearTracking() => _trackedPages.Clear();

    /// <summary>Returns a copy of the page's current bytes, loading it if needed.</summary>
    public byte[] ReadCopy(long pageId)
    {
        using PinnedPage page = Pin(pageId);
        return page.Data.ToArray();
    }

    internal void MarkDirty(PageFrame frame)
    {
        frame.IsDirty = true;
        _trackedPages.Add(frame.PageId);
    }

    public void FlushAll()
    {
        foreach (PageFrame frame in _cached.Values.Where(f => f.IsDirty))
        {
            _store.WritePage(frame.PageId, frame.Data);
            frame.IsDirty = false;
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

    private PageFrame LoadFrame(long pageId)
    {
        PageFrame frame = TakeFrame();
        _store.ReadPage(pageId, frame.Data);
        frame.PageId = pageId;
        frame.IsDirty = false;
        _cached.Add(pageId, frame);
        _lru.AddFirst(frame.Node);
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
        Remove(victim);
        return victim;
    }

    private PageFrame? FindEvictable()
    {
        for (LinkedListNode<PageFrame>? node = _lru.Last; node is not null; node = node.Previous)
        {
            if (node.Value.PinCount == 0 && !node.Value.IsDirty)
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
    }
}
