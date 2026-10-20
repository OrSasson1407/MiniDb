using System.Runtime.InteropServices;

namespace MiniDb.Api;

/// <summary>
/// A reader-writer lock whose read side does not contend. Each thread counts its read locks in its own cache line,
/// so many readers never write to the same memory. A writer excludes other writers with a monitor, tells new readers to
/// wait, and waits for the readers that are already inside. Writing is therefore slower than with
/// <see cref="ReaderWriterLockSlim"/>, which suits a database that reads far more often than it writes.
/// Not re-entrant: a thread that holds the read lock cannot take the write lock, and a thread cannot take the same lock twice.
/// A read lock must be released on the thread that took it.
/// </summary>
internal sealed class StripedReadWriteLock
{
    private const int MaxStripes = 64;
    private const int TrackedLocksPerThread = 4;

    private static int s_nextStripe;

    [ThreadStatic]
    private static int t_stripe;

    [ThreadStatic]
    private static StripedReadWriteLock?[]? t_heldForReading;

    private readonly Stripe[] _stripes;
    private readonly int _stripeMask;
    private readonly object _writerGate = new();
    private int _writerPending;

    public StripedReadWriteLock()
    {
        int stripes = (int)Math.Min(MaxStripes, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Environment.ProcessorCount * 2));
        _stripes = new Stripe[stripes];
        _stripeMask = stripes - 1;
    }

    public bool IsWriteLockHeld => Monitor.IsEntered(_writerGate);

    public void EnterReadLock()
    {
        int stripe = StripeOfThread();
        while (true)
        {
            Interlocked.Increment(ref _stripes[stripe].Readers);
            if (Volatile.Read(ref _writerPending) == 0)
            {
                RememberRead();
                return;
            }

            Interlocked.Decrement(ref _stripes[stripe].Readers);
            WaitForWriter();
        }
    }

    public void ExitReadLock()
    {
        ForgetRead();
        Interlocked.Decrement(ref _stripes[StripeOfThread()].Readers);
    }

    public void EnterWriteLock()
    {
        if (IsWriteLockHeld || HoldsRead())
        {
            throw new LockRecursionException("The database lock is not re-entrant.");
        }

        Monitor.Enter(_writerGate);
        Interlocked.Exchange(ref _writerPending, 1);
        var wait = new SpinWait();
        while (ActiveReaders() != 0)
        {
            wait.SpinOnce();
        }
    }

    public void ExitWriteLock()
    {
        Volatile.Write(ref _writerPending, 0);
        Monitor.Exit(_writerGate);
    }

    /// <summary>Blocks until the writer that is pending or active releases the lock.</summary>
    private void WaitForWriter()
    {
        if (IsWriteLockHeld)
        {
            throw new LockRecursionException("A thread that holds the write lock cannot take the read lock.");
        }

        lock (_writerGate)
        {
        }
    }

    private int ActiveReaders()
    {
        int total = 0;
        for (int i = 0; i < _stripes.Length; i++)
        {
            total += Volatile.Read(ref _stripes[i].Readers);
        }

        return total;
    }

    private int StripeOfThread()
    {
        int stripe = t_stripe;
        if (stripe == 0)
        {
            stripe = t_stripe = Interlocked.Increment(ref s_nextStripe);
        }

        return (stripe - 1) & _stripeMask;
    }

    private void RememberRead()
    {
        StripedReadWriteLock?[] held = t_heldForReading ??= new StripedReadWriteLock?[TrackedLocksPerThread];
        for (int i = 0; i < held.Length; i++)
        {
            if (held[i] is null)
            {
                held[i] = this;
                return;
            }
        }
    }

    private void ForgetRead()
    {
        StripedReadWriteLock?[]? held = t_heldForReading;
        if (held is null)
        {
            return;
        }

        for (int i = 0; i < held.Length; i++)
        {
            if (ReferenceEquals(held[i], this))
            {
                held[i] = null;
                return;
            }
        }
    }

    private bool HoldsRead()
    {
        StripedReadWriteLock?[]? held = t_heldForReading;
        return held is not null && Array.IndexOf(held, this) >= 0;
    }

    /// <summary>A reader count alone in its cache line, so counts of different threads never share one.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct Stripe
    {
        [FieldOffset(64)]
        public int Readers;
    }
}
