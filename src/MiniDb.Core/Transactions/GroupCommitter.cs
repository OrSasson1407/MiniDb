using System.Diagnostics;

namespace MiniDb.Transactions;

/// <summary>
/// Decides when log records are durable, and shares one <c>fsync</c> between every commit that is waiting for it.
///
/// A committing thread appends its records under the database's write lock, releases that lock, and then calls
/// <see cref="AwaitDurable"/>. The first waiter that finds no sync running becomes the leader. Before it syncs, the leader
/// gives writers that are already in the commit pipeline a short window to append, so that one sync covers them too.
/// The window is only used when such writers exist; a lone writer never waits for it.
///
/// A failed sync is final. The log may have dropped the records of commits that were already written, so the engine stops
/// accepting work and the database must be reopened, which recovers from the log. Retrying the sync would not be safe.
/// </summary>
internal sealed class GroupCommitter
{
    /// <summary>How long a leader waits for writers in the pipeline to append before it syncs.</summary>
    private static readonly long GatherTicks = Stopwatch.Frequency / 1_000_000 * 150;

    private readonly WriteAheadLog _log;
    private readonly object _lock = new();
    private long _written;
    private long _durable;
    private bool _syncing;
    private Exception? _failure;

    /// <summary>Writers that have started a commit and have not finished it (durable or failed).</summary>
    private int _inPipeline;

    /// <summary>Threads currently inside <see cref="AwaitDurable"/> and not yet released by it.</summary>
    private int _awaiting;

    public GroupCommitter(WriteAheadLog log)
    {
        _log = log;
        _written = log.Position;
        _durable = log.Position;
    }

    /// <summary>Position of the last record that has been written to the log, durable or not.</summary>
    public long Written
    {
        get
        {
            lock (_lock)
            {
                return _written;
            }
        }
    }

    /// <summary>Called by a writer before it takes the write lock for a commit.</summary>
    public void EnterPipeline() => Interlocked.Increment(ref _inPipeline);

    /// <summary>Called by a writer when its commit is finished, whether or not it succeeded.</summary>
    public void LeavePipeline() => Interlocked.Decrement(ref _inPipeline);

    /// <summary>Records that everything up to <paramref name="position"/> has been written to the log.</summary>
    public void MarkWritten(long position)
    {
        lock (_lock)
        {
            _written = Math.Max(_written, position);
        }
    }

    /// <summary>Records that everything up to <paramref name="position"/> is durable without a sync: a checkpoint has copied it.</summary>
    public void MarkDurable(long position)
    {
        lock (_lock)
        {
            _durable = Math.Max(_durable, position);
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Throws if an earlier sync failed, so that no new work starts on a log that may have lost records.</summary>
    public void ThrowIfFailed()
    {
        lock (_lock)
        {
            ThrowIfFailedLocked();
        }
    }

    /// <summary>Returns once everything up to <paramref name="position"/> is durable, syncing the log if needed.</summary>
    public void AwaitDurable(long position)
    {
        Interlocked.Increment(ref _awaiting);
        try
        {
            AwaitDurableCore(position);
        }
        finally
        {
            Interlocked.Decrement(ref _awaiting);
        }
    }

    private void AwaitDurableCore(long position)
    {
        while (true)
        {
            lock (_lock)
            {
                ThrowIfFailedLocked();
                if (_durable >= position)
                {
                    return;
                }

                if (_syncing)
                {
                    Monitor.Wait(_lock);
                    continue;
                }

                _syncing = true;
            }

            GatherWriters();

            // Everything appended so far is made durable, including records of a commit still in progress (a page
            // write-back can force them out early). The position is read after the window, so writers gathered in it count.
            long target = _log.Position;
            Exception? error = null;
            try
            {
                _log.Sync();
            }
            catch (Exception ex)
            {
                error = ex;
            }

            lock (_lock)
            {
                _syncing = false;
                if (error is null)
                {
                    _durable = Math.Max(_durable, target);
                }
                else
                {
                    _failure = error;
                }

                Monitor.PulseAll(_lock);
            }

            if (error is not null)
            {
                throw new IOException("The write-ahead log could not be synced. The database stopped accepting work; reopen it.", error);
            }
        }
    }

    /// <summary>
    /// Waits, for a short time, until every writer in the pipeline has reached <see cref="AwaitDurable"/>. Writers that are
    /// still appending will then be covered by this sync. Nothing is waited for when no other writer is in the pipeline.
    /// </summary>
    private void GatherWriters()
    {
        long deadline = Stopwatch.GetTimestamp() + GatherTicks;
        while (Volatile.Read(ref _inPipeline) > Volatile.Read(ref _awaiting) && Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Yield();
        }
    }

    private void ThrowIfFailedLocked()
    {
        if (_failure is not null)
        {
            throw new IOException("The database stopped after a failed write-ahead log sync; reopen it.", _failure);
        }
    }
}
