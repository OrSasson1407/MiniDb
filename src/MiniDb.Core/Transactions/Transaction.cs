using MiniDb.Storage;

namespace MiniDb.Transactions;

/// <summary>
/// One unit of work. Changes stay in the buffer pool until <see cref="Commit"/> logs them to the WAL.
/// Disposing an unfinished transaction rolls it back.
/// </summary>
public sealed class Transaction : IDisposable
{
    private readonly TransactionManager _manager;
    private readonly BufferPool _pool;
    private bool _finished;

    internal Transaction(TransactionManager manager, BufferPool pool, long id)
    {
        _manager = manager;
        _pool = pool;
        Id = id;
    }

    public long Id { get; }

    /// <summary>True once the transaction has been committed or rolled back.</summary>
    public bool IsFinished => _finished;

    /// <summary>Runs once when the transaction commits or rolls back. The database uses it to release its write lock.</summary>
    internal Action? Finished { get; set; }

    public void WritePage(long pageId, ReadOnlySpan<byte> data)
    {
        EnsureActive();
        using PinnedPage page = _pool.Pin(pageId);
        data.CopyTo(page.Data);
        page.MarkDirty();
    }

    /// <summary>Returns a copy of the page as this transaction currently sees it.</summary>
    public byte[] ReadPage(long pageId)
    {
        EnsureActive();
        return _pool.ReadCopy(pageId);
    }

    public void Commit()
    {
        long position = CommitWithoutWaiting();
        _manager.AwaitDurable(position);
    }

    /// <summary>
    /// Writes the commit records and releases the write lock (through <see cref="Finished"/>), but does not wait for the log
    /// to reach the disk. Returns the log position to wait for with <see cref="AwaitDurable"/>. Splitting the two lets other
    /// writers append while this commit waits for a shared sync.
    /// </summary>
    internal long CommitWithoutWaiting()
    {
        EnsureActive();
        _finished = true;
        try
        {
            return _manager.CommitAppend(this);
        }
        finally
        {
            Finished?.Invoke();
        }
    }

    public void Rollback()
    {
        EnsureActive();
        _finished = true;
        try
        {
            _manager.Rollback(this);
        }
        finally
        {
            Finished?.Invoke();
        }
    }

    public void Dispose()
    {
        if (!_finished)
        {
            Rollback();
        }
    }

    private void EnsureActive()
    {
        if (_finished)
        {
            throw new InvalidOperationException("The transaction is already finished.");
        }
    }
}
