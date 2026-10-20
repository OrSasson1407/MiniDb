namespace MiniDb.Storage;

/// <summary>
/// LRU page cache with a fixed number of frames, split into partitions. Each partition has its own lock and its own
/// LRU list, so readers working on different pages rarely wait for each other. A page changed by the active
/// transaction is never evicted or written, so uncommitted data never reaches the data file (no-steal). A page changed
/// by a committed transaction may be written back at any time, because its change is already in the WAL.
/// </summary>
public sealed class BufferPool : IBufferPool
{
    /// <summary>Partitions are only added when each one still has enough frames to hold a scan's working set.</summary>
    private const int MinFramesPerPartition = 64;
    private const int MaxPartitions = 16;

    private readonly IPageStore _store;
    private readonly Partition[] _partitions;
    private readonly HashSet<long> _trackedPages = new();
    private readonly List<PageFrame> _snapshotted = new();
    private bool _tracking;

    public BufferPool(IPageStore store, int capacity = 256)
    {
        _store = store;
        Capacity = capacity;
        int count = Math.Clamp(capacity / MinFramesPerPartition, 1, MaxPartitions);
        _partitions = new Partition[count];
        for (int i = 0; i < count; i++)
        {
            _partitions[i] = new Partition(capacity / count + (i < capacity % count ? 1 : 0));
        }
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
        Partition partition = PartitionOf(pageId);
        lock (partition.Gate)
        {
            return PinCore(partition, pageId, sequential);
        }
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
        if (PartitionOf(pageId).Cached.TryGetValue(pageId, out PageFrame? frame) && frame.HasSnapshot)
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
            if (PartitionOf(pageId).Cached.TryGetValue(pageId, out PageFrame? frame))
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
            if (PartitionOf(pageId).Cached.TryGetValue(pageId, out PageFrame? frame) && frame.HasSnapshot)
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
        foreach (Partition partition in _partitions)
        {
            lock (partition.Gate)
            {
                foreach (PageFrame frame in partition.Cached.Values.Where(f => f.IsDirty && !f.TxDirty))
                {
                    WriteBack(frame);
                }
            }
        }
    }

    public void Discard(long pageId)
    {
        Partition partition = PartitionOf(pageId);
        lock (partition.Gate)
        {
            if (!partition.Cached.TryGetValue(pageId, out PageFrame? frame))
            {
                return;
            }

            if (frame.PinCount > 0)
            {
                throw new InvalidOperationException($"Page {pageId} is pinned and cannot be discarded.");
            }

            Remove(partition, frame);
            frame.IsDirty = false;
            partition.FreeFrames.Push(frame);
        }
    }

    /// <summary>Intentionally does not flush: the transaction layer decides what reaches disk.</summary>
    public void Dispose()
    {
    }

    /// <summary>Loads a page without pinning it, so a later sequential Pin finds it in the cache.</summary>
    public void Prefetch(long pageId)
    {
        Partition partition = PartitionOf(pageId);
        lock (partition.Gate)
        {
            if (!partition.Cached.ContainsKey(pageId))
            {
                LoadFrame(partition, pageId, sequential: true);
            }
        }
    }

    internal void Unpin(PageFrame frame)
    {
        lock (frame.Gate!)
        {
            if (frame.PinCount > 0)
            {
                frame.PinCount--;
            }
        }
    }

    private Partition PartitionOf(long pageId) => _partitions[pageId % _partitions.Length];

    private PinnedPage PinCore(Partition partition, long pageId, bool sequential)
    {
        PageFrame frame;
        if (partition.Cached.TryGetValue(pageId, out PageFrame? cached))
        {
            frame = cached;
            if (!sequential)
            {
                Touch(partition, frame);
            }
        }
        else
        {
            frame = LoadFrame(partition, pageId, sequential);
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

    private PageFrame LoadFrame(Partition partition, long pageId, bool sequential)
    {
        PageFrame frame = TakeFrame(partition);
        try
        {
            _store.ReadPage(pageId, frame.Data);
            PageChecksum.Verify(frame.Data, pageId);
        }
        catch
        {
            partition.FreeFrames.Push(frame);
            throw;
        }

        frame.PageId = pageId;
        frame.IsDirty = false;
        frame.TxDirty = false;
        frame.HasSnapshot = false;
        partition.Cached.Add(pageId, frame);
        if (sequential)
        {
            partition.Lru.AddLast(frame.Node);
        }
        else
        {
            partition.Lru.AddFirst(frame.Node);
        }

        return frame;
    }

    private PageFrame TakeFrame(Partition partition)
    {
        if (partition.FreeFrames.Count > 0)
        {
            return partition.FreeFrames.Pop();
        }

        if (partition.Frames.Count < partition.Capacity)
        {
            var created = new PageFrame { Gate = partition.Gate };
            partition.Frames.Add(created);
            return created;
        }

        PageFrame victim = FindEvictable(partition) ?? throw new BufferPoolFullException(Capacity);
        if (victim.IsDirty)
        {
            WriteBack(victim);
        }

        Remove(partition, victim);
        return victim;
    }

    private static PageFrame? FindEvictable(Partition partition)
    {
        for (LinkedListNode<PageFrame>? node = partition.Lru.Last; node is not null; node = node.Previous)
        {
            if (node.Value.PinCount == 0 && !node.Value.TxDirty)
            {
                return node.Value;
            }
        }

        return null;
    }

    private static void Touch(Partition partition, PageFrame frame)
    {
        partition.Lru.Remove(frame.Node);
        partition.Lru.AddFirst(frame.Node);
    }

    private static void Remove(Partition partition, PageFrame frame)
    {
        partition.Cached.Remove(frame.PageId);
        partition.Lru.Remove(frame.Node);
        frame.HasSnapshot = false;
        frame.TxDirty = false;
    }

    /// <summary>The frames and index of one slice of the pool. Its lock guards everything in it.</summary>
    private sealed class Partition
    {
        public Partition(int capacity)
        {
            Capacity = capacity;
        }

        public int Capacity { get; }

        public object Gate { get; } = new();

        public Dictionary<long, PageFrame> Cached { get; } = new();

        public LinkedList<PageFrame> Lru { get; } = new();

        public List<PageFrame> Frames { get; } = new();

        public Stack<PageFrame> FreeFrames { get; } = new();
    }
}
