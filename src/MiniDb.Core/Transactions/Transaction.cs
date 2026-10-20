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
        EnsureActive();
        _finished = true;
        _manager.Commit(this);
    }

    public void Rollback()
    {
        EnsureActive();
        _finished = true;
        _manager.Rollback(this);
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
