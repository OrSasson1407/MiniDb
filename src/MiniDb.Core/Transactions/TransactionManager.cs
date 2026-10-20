using MiniDb.Storage;

namespace MiniDb.Transactions;

/// <summary>
/// Runs write transactions one at a time. A commit makes the transaction's changes durable in the WAL; the
/// changed pages reach the data file later, on eviction or at checkpoint. Rollback restores the changed pages.
/// </summary>
public sealed class TransactionManager
{
    private readonly IPageStore _store;
    private readonly BufferPool _pool;
    private readonly WriteAheadLog _wal;
    private readonly bool _fsyncOnCommit;
    private readonly long _checkpointThresholdBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _nextTransactionId = 1;
    private bool _walUnsynced;
    private readonly byte[] _deltaScratch = new byte[PageConstants.PageSize * 2];

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
        _fsyncOnCommit = fsyncOnCommit;
        _checkpointThresholdBytes = checkpointThresholdBytes;
        _pool.BeforeWriteBack = EnsureWalDurable;
    }

    /// <summary>Starts a write transaction. Only one may be open at a time.</summary>
    public Transaction Begin()
    {
        if (!_gate.Wait(0))
        {
            throw new InvalidOperationException("Another transaction is open. Only one writer is allowed at a time.");
        }

        _pool.BeginTracking();
        return new Transaction(this, _pool, _nextTransactionId++);
    }

    /// <summary>
    /// Copies every changed page into the WAL, then a commit record. With FsyncOnCommit the log is forced to disk
    /// before this returns; otherwise it is forced before any page is written to the data file.
    /// </summary>
    internal void Commit(Transaction transaction)
    {
        try
        {
            WriteCommitRecords(transaction);
            _pool.CommitTracking();
        }
        finally
        {
            _gate.Release();
        }

        if (_wal.Length > _checkpointThresholdBytes)
        {
            Checkpoint();
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
            _walUnsynced = true;
            if (_fsyncOnCommit)
            {
                EnsureWalDurable();
            }
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
            _walUnsynced = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forces the log to disk if it has records that are not yet durable. Runs before any page write.</summary>
    private void EnsureWalDurable()
    {
        if (_walUnsynced)
        {
            _wal.Sync();
            _walUnsynced = false;
        }
    }

    private void DiscardChanges()
    {
        _pool.RollbackTracking();
        RolledBack?.Invoke();
    }
}
