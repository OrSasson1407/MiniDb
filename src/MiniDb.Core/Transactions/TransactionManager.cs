using MiniDb.Storage;

namespace MiniDb.Transactions;

/// <summary>
/// Runs write transactions one at a time. A commit writes the transaction's records to the WAL; the changed pages reach the
/// data file later, on eviction or at checkpoint. Rollback restores the changed pages.
///
/// A commit is split in two. <see cref="CommitAppend"/> writes the records while the caller holds the database's write lock,
/// and <see cref="AwaitDurable"/> waits for them to reach the disk after that lock is released, so several commits can share
/// one sync (see <see cref="GroupCommitter"/>).
/// </summary>
public sealed class TransactionManager
{
    private readonly IPageStore _store;
    private readonly BufferPool _pool;
    private readonly WriteAheadLog _wal;
    private readonly GroupCommitter _committer;
    private readonly bool _fsyncOnCommit;
    private readonly long _checkpointThresholdBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _nextTransactionId = 1;
    private readonly byte[] _deltaScratch = new byte[PageConstants.PageSize * 2];

    /// <summary>Pages appended by a transaction that did not commit. They are put on the free list at the next chance.</summary>
    private readonly List<long> _orphanedPages = new();

    /// <summary>Raised after every rollback, including the rollback of a failed commit.</summary>
    public event Action? RolledBack;

    public TransactionManager(
        IPageStore store,
        BufferPool pool,
        WriteAheadLog wal,
        bool fsyncOnCommit = true,
        long checkpointThresholdBytes = long.MaxValue)
    {
        _store = store;
        _pool = pool;
        _wal = wal;
        _committer = new GroupCommitter(wal);
        _fsyncOnCommit = fsyncOnCommit;
        _checkpointThresholdBytes = checkpointThresholdBytes;
        _pool.BeforeWriteBack = EnsureWalDurable;
    }

    /// <summary>Starts a write transaction. Only one may be open at a time.</summary>
    public Transaction Begin()
    {
        _committer.ThrowIfFailed();
        if (!_gate.Wait(0))
        {
            throw new InvalidOperationException("Another transaction is open. Only one writer is allowed at a time.");
        }

        _pool.BeginTracking();
        return new Transaction(this, _pool, _nextTransactionId++);
    }

    /// <summary>
    /// Writes every changed page into the WAL, then a commit record, and releases the transaction. Returns the log position
    /// that must be durable before the commit may be reported. The caller holds the database's write lock.
    /// </summary>
    internal long CommitAppend(Transaction transaction)
    {
        long position;
        try
        {
            WriteCommitRecords(transaction);
            _pool.CommitTracking();
            position = _wal.Position;
            _committer.MarkWritten(position);
        }
        finally
        {
            _gate.Release();
        }

        ReturnOrphanedPages();
        if (_wal.Length > _checkpointThresholdBytes)
        {
            Checkpoint();
        }

        return position;
    }

    /// <summary>Marks a writer as on its way to commit, so a sync leader can wait briefly for it. Pair with <see cref="LeaveCommitPipeline"/>.</summary>
    internal void EnterCommitPipeline() => _committer.EnterPipeline();

    internal void LeaveCommitPipeline() => _committer.LeavePipeline();

    /// <summary>
    /// Waits until the log is durable up to <paramref name="position"/>. Called after the write lock is released, so other
    /// writers can append while this one waits. Does nothing when FsyncOnCommit is off.
    /// </summary>
    internal void AwaitDurable(long position)
    {
        if (_fsyncOnCommit)
        {
            _committer.AwaitDurable(position);
        }
    }

    /// <summary>
    /// Readers call this after taking the shared lock. Everything written so far must be durable before a reader may see it,
    /// otherwise a reader could return data that a crash then takes back.
    /// </summary>
    internal void AwaitVisibleDurable()
    {
        if (_fsyncOnCommit)
        {
            _committer.AwaitDurable(_committer.Written);
        }
    }

    /// <summary>Logs only the bytes that changed in a page, or the whole page when no before-image exists.</summary>
    private void AppendPage(long transactionId, long pageId)
    {
        if (_pool.TryGetBeforeImage(pageId, out ReadOnlySpan<byte> before, out ReadOnlySpan<byte> current))
        {
            int length = PageDelta.TryCompute(before, current, _deltaScratch);
            if (length >= 0)
            {
                byte[] payload = _deltaScratch.AsSpan(0, length).ToArray();
                _wal.Append(new WalFrame(transactionId, WalFrameType.PageDelta, pageId, payload));
                return;
            }
        }

        byte[] image = _pool.ReadCopy(pageId);
        PageChecksum.Stamp(image);
        _wal.Append(new WalFrame(transactionId, WalFrameType.PageImage, pageId, image));
    }

    private void WriteCommitRecords(Transaction transaction)
    {
        long mark = _wal.Length;
        try
        {
            foreach (long pageId in _pool.TrackedPages.ToArray())
            {
                AppendPage(transaction.Id, pageId);
            }

            _wal.Append(new WalFrame(transaction.Id, WalFrameType.Commit, 0, Array.Empty<byte>()));
        }
        catch
        {
            // Cut off the partly written commit so recovery never replays it.
            _wal.TruncateTo(mark);
            DiscardChanges();
            throw;
        }
    }

    internal void Rollback(Transaction transaction)
    {
        try
        {
            DiscardChanges();
        }
        finally
        {
            _gate.Release();
        }

        ReturnOrphanedPages();
    }

    /// <summary>
    /// Puts the pages that a rolled-back transaction appended onto the free list, in a small transaction of their own.
    /// Runs after the gate is released, while the caller still holds the database's write lock.
    /// </summary>
    private void ReturnOrphanedPages()
    {
        if (_orphanedPages.Count == 0)
        {
            return;
        }

        long[] pages = _orphanedPages.ToArray();
        _orphanedPages.Clear();
        using Transaction freeing = Begin();
        foreach (long pageId in pages)
        {
            FreePages.Push(_pool, pageId);
        }

        freeing.Commit();
    }

    /// <summary>
    /// Writes every committed page to the data file, syncs it, and empties the WAL.
    /// Must not be called while a transaction is open.
    /// </summary>
    public void Checkpoint()
    {
        if (!_gate.Wait(0))
        {
            throw new InvalidOperationException("Checkpoint cannot run while a transaction is open.");
        }

        try
        {
            _pool.FlushAll();
            _store.Sync();
            _wal.Truncate();
            // Everything the log held is now in the data file, so it counts as durable.
            _committer.MarkDurable(_wal.Position);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Makes the log durable up to everything written so far. Runs before any page write.</summary>
    private void EnsureWalDurable() => _committer.AwaitDurable(_wal.Position);

    private void DiscardChanges()
    {
        _orphanedPages.AddRange(_pool.RollbackTracking());
        RolledBack?.Invoke();
    }
}
