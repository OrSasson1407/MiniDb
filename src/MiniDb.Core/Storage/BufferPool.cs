using System.Collections.Concurrent;
using System.Threading;

namespace MiniDb.Storage;

/// <summary>
/// LRU page cache with a fixed number of frames, split into partitions. A cache hit takes no lock: the page is pinned
/// with an atomic counter, and a frame that is being evicted is marked so that a pin fails and falls back to the lock.
/// Loading and evicting take the partition's lock, and each partition has its own LRU list. A page changed by the active
/// transaction is never evicted or written, so uncommitted data never reaches the data file (no-steal). A page changed
/// by a committed transaction may be written back at any time, because its change is already in the WAL.
/// A transaction that changes more pages than the pool holds makes the pool grow for as long as it runs.
/// </summary>
public sealed class BufferPool : IBufferPool
{
    /// <summary>Partitions are only added when each one still has enough frames to hold a scan's working set.</summary>
    private const int MinFramesPerPartition = 64;
    private const int MaxPartitions = 16;

    /// <summary>A page pinned this many times becomes sticky (see <see cref="PageFrame.Sticky"/>).</summary>
    private const int StickyHits = 128;

    /// <summary>
    /// When every frame of a partition is pinned or holds an uncommitted change, the partition grows instead of failing,
    /// up to this many times its normal size (and at least <see cref="MinOverflowFrames"/> frames). It shrinks back afterwards.
    /// </summary>
    private const int OverflowFactor = 8;

    private const int MinOverflowFrames = 16;

    /// <summary>The LRU list is updated, and stickiness checked, once per this many pins of a page.</summary>
    private const int TouchInterval = 16;

    /// <summary>A frame whose pin count is this value is being evicted or was freed; nobody may pin it.</summary>
    private const int Claimed = -1;

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
        if (partition.Cached.TryGetValue(pageId, out PageFrame? hit))
        {
            if (Volatile.Read(ref hit.Sticky) && hit.PageId == pageId)
            {
                return Track(hit, counted: false);
            }

            if (TryPin(hit, pageId))
            {
                if (!sequential)
                {
                    RecordHit(partition, hit);
                }

                return Track(hit, counted: true);
            }
        }

        lock (partition.Gate)
        {
            return PinSlow(partition, pageId, sequential);
        }
    }

    /// <summary>
    /// Counts a pin. Only one in <see cref="TouchInterval"/> takes the partition lock: it moves the page to the front of
    /// the LRU list, or makes it sticky once it is pinned very often.
    /// </summary>
    private static void RecordHit(Partition partition, PageFrame frame)
    {
        int hits = ++frame.Hits;
        if ((hits & (TouchInterval - 1)) != 0)
        {
            return;
        }

        lock (partition.Gate)
        {
            if (frame.Sticky || frame.Node.List is null)
            {
                return;
            }

            if (hits >= StickyHits && partition.StickyCount < partition.Capacity / 8)
            {
                partition.Lru.Remove(frame.Node);
                frame.Sticky = true;
                partition.StickyCount++;
                return;
            }

            partition.Lru.Remove(frame.Node);
            partition.Lru.AddFirst(frame.Node);
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

            if (Interlocked.CompareExchange(ref frame.PinCount, Claimed, 0) != 0)
            {
                throw new InvalidOperationException($"Page {pageId} is pinned and cannot be discarded.");
            }

            if (frame.Sticky)
            {
                frame.Sticky = false;
                partition.StickyCount--;
                partition.Lru.AddLast(frame.Node);
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
        Interlocked.Decrement(ref frame.PinCount);
    }

    private Partition PartitionOf(long pageId) => _partitions[pageId % _partitions.Length];

    /// <summary>
    /// Adds one pin to a frame if it is not being evicted, and checks that it still holds the page we asked for:
    /// a reader may have looked the frame up just before it was reused for another page.
    /// </summary>
    private static bool TryPin(PageFrame frame, long pageId)
    {
        int current;
        do
        {
            current = Volatile.Read(ref frame.PinCount);
            if (current < 0)
            {
                return false;
            }
        }
        while (Interlocked.CompareExchange(ref frame.PinCount, current + 1, current) != current);

        if (frame.PageId == pageId)
        {
            return true;
        }

        Interlocked.Decrement(ref frame.PinCount);
        return false;
    }

    /// <summary>Caller holds the partition lock. Loads the page if needed and pins it.</summary>
    private PinnedPage PinSlow(Partition partition, long pageId, bool sequential)
    {
        PageFrame frame;
        if (partition.Cached.TryGetValue(pageId, out PageFrame? cached))
        {
            frame = cached;
            if (frame.Sticky)
            {
                return Track(frame, counted: false);
            }

            if (!sequential)
            {
                Touch(partition, frame);
            }

            // No evictor can claim the frame while we hold the partition lock, so a plain increment is safe.
            Interlocked.Increment(ref frame.PinCount);
        }
        else
        {
            frame = LoadFrame(partition, pageId, sequential);
            Interlocked.Increment(ref frame.PinCount);
        }

        return Track(frame, counted: true);
    }

    /// <summary>Remembers the page as it was before the active transaction first pinned it, so rollback can restore it.</summary>
    private PinnedPage Track(PageFrame frame, bool counted)
    {
        if (_tracking && !frame.HasSnapshot)
        {
            frame.SnapshotBuffer ??= new byte[PageConstants.PageSize];
            frame.Data.AsSpan().CopyTo(frame.SnapshotBuffer);
            frame.HasSnapshot = true;
            frame.SnapshotWasDirty = frame.IsDirty;
            _snapshotted.Add(frame);
        }

        return new PinnedPage(this, frame, counted);
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
        ShrinkAfterTransaction();
    }

    private void WriteBack(PageFrame frame)
    {
        BeforeWriteBack?.Invoke();
        PageChecksum.Stamp(frame.Data);
        _store.WritePage(frame.PageId, frame.Data);
        frame.IsDirty = false;
    }

    /// <summary>Caller holds the partition lock. Reads the page into a frame and publishes it as unpinned.</summary>
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
        frame.Hits = 0;
        Volatile.Write(ref frame.PinCount, 0);
        partition.Cached[pageId] = frame;
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

    /// <summary>Returns a frame that nobody can pin: a free one, a new one, or a claimed victim.</summary>
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

        PageFrame? victim = ClaimVictim(partition);
        if (victim is null)
        {
            return CreateOverflowFrame(partition);
        }

        if (victim.IsDirty)
        {
            WriteBack(victim);
        }

        Remove(partition, victim);
        if (partition.Frames.Count > partition.Capacity)
        {
            ShrinkPartition(partition, maxDrops: 2);
        }

        return victim;
    }

    /// <summary>Caller holds the partition lock. Adds a frame beyond the partition's capacity, or fails at the hard limit.</summary>
    private PageFrame CreateOverflowFrame(Partition partition)
    {
        if (partition.Frames.Count >= Math.Max(partition.Capacity * OverflowFactor, MinOverflowFrames))
        {
            throw new BufferPoolFullException(Capacity);
        }

        var created = new PageFrame { Gate = partition.Gate };
        partition.Frames.Add(created);
        return created;
    }

    /// <summary>
    /// Caller holds the partition lock. Drops frames above the partition's capacity, starting with free ones and then
    /// the least recently used unpinned pages, until <paramref name="maxDrops"/> frames are gone.
    /// </summary>
    private void ShrinkPartition(Partition partition, int maxDrops)
    {
        var dropped = new HashSet<PageFrame>();
        int excess = Math.Min(partition.Frames.Count - partition.Capacity, maxDrops);
        while (excess > 0 && partition.FreeFrames.Count > 0)
        {
            dropped.Add(partition.FreeFrames.Pop());
            excess--;
        }

        while (excess > 0 && ClaimVictim(partition) is { } victim)
        {
            if (victim.IsDirty)
            {
                WriteBack(victim);
            }

            Remove(partition, victim);
            dropped.Add(victim);
            excess--;
        }

        if (dropped.Count > 0)
        {
            partition.Frames.RemoveAll(dropped.Contains);
        }
    }

    /// <summary>Gives back the memory a large transaction needed. Runs once the transaction has ended.</summary>
    private void ShrinkAfterTransaction()
    {
        foreach (Partition partition in _partitions)
        {
            if (partition.Frames.Count <= partition.Capacity)
            {
                continue;
            }

            lock (partition.Gate)
            {
                ShrinkPartition(partition, maxDrops: int.MaxValue);
            }
        }
    }

    /// <summary>Finds the least recently used frame nobody pins, and claims it so that no reader can pin it meanwhile.</summary>
    private static PageFrame? ClaimVictim(Partition partition)
    {
        for (LinkedListNode<PageFrame>? node = partition.Lru.Last; node is not null; node = node.Previous)
        {
            PageFrame frame = node.Value;
            if (Interlocked.CompareExchange(ref frame.PinCount, Claimed, 0) != 0)
            {
                continue;
            }

            if (frame.TxDirty)
            {
                Volatile.Write(ref frame.PinCount, 0);
                continue;
            }

            return frame;
        }

        return null;
    }

    private static void Touch(Partition partition, PageFrame frame)
    {
        partition.Lru.Remove(frame.Node);
        partition.Lru.AddFirst(frame.Node);
    }

    /// <summary>Takes a claimed frame out of its partition. The frame stays claimed until it is loaded again.</summary>
    private static void Remove(Partition partition, PageFrame frame)
    {
        partition.Cached.TryRemove(frame.PageId, out _);
        partition.Lru.Remove(frame.Node);
        frame.HasSnapshot = false;
        frame.TxDirty = false;
    }

    /// <summary>The frames and index of one slice of the pool. Its lock guards loading, eviction and the LRU list.</summary>
    private sealed class Partition
    {
        public Partition(int capacity)
        {
            Capacity = capacity;
        }

        public int Capacity { get; }

        public object Gate { get; } = new();

        public ConcurrentDictionary<long, PageFrame> Cached { get; } = new();

        public LinkedList<PageFrame> Lru { get; } = new();

        public List<PageFrame> Frames { get; } = new();

        public Stack<PageFrame> FreeFrames { get; } = new();

        /// <summary>Frames that are sticky. Guarded by the partition lock.</summary>
        public int StickyCount { get; set; }
    }
}
