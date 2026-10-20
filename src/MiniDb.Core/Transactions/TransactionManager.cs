using MiniDb.Storage;

namespace MiniDb.Transactions;

/// <summary>
/// Runs write transactions one at a time. Each transaction starts from a flushed pool, so the pool's
/// dirty pages are exactly the active transaction's changes: rollback discards them and commit logs them.
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
    }

    /// <summary>Starts a write transaction. Only one may be open at a time.</summary>
    public Transaction Begin()
    {
        if (!_gate.Wait(0))
        {
            throw new InvalidOperationException("Another transaction is open. Only one writer is allowed at a time.");
        }

        try
        {
            // Committed pages from earlier transactions reach the data file here. The WAL keeps them until checkpoint.
            _pool.FlushAll();
            _pool.ClearTracking();
        }
        catch
        {
            _gate.Release();
            throw;
        }

        return new Transaction(this, _pool, _nextTransactionId++);
    }

    /// <summary>
    /// Copies every changed page into the WAL, then a commit record, and makes them durable.
    /// Once the commit record is durable, the changed pages are also written to the data file.
    /// </summary>
    internal void Commit(Transaction transaction)
    {
        try
        {
            WriteCommitRecords(transaction);
            _pool.ClearTracking();
            _pool.FlushAll();
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

    private void WriteCommitRecords(Transaction transaction)
    {
        long mark = _wal.Length;
        try
        {
            foreach (long pageId in _pool.TrackedPages.ToArray())
            {
                byte[] image = _pool.ReadCopy(pageId);
                PageChecksum.Stamp(image);
                _wal.Append(new WalFrame(transaction.Id, WalFrameType.PageImage, pageId, image));
            }

            _wal.Append(new WalFrame(transaction.Id, WalFrameType.Commit, 0, Array.Empty<byte>()));
            if (_fsyncOnCommit)
            {
                _wal.Sync();
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
        }
        finally
        {
            _gate.Release();
        }
    }

    private void DiscardChanges()
    {
        foreach (long pageId in _pool.TrackedPages.ToArray())
        {
            _pool.Discard(pageId);
        }

        _pool.ClearTracking();
    }
}
