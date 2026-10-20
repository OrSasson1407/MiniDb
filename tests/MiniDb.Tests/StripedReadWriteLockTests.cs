using MiniDb.Api;

namespace MiniDb.Tests;

public class StripedReadWriteLockTests
{
    [Fact]
    public async Task Readers_ShareTheLock()
    {
        var gate = new StripedReadWriteLock();
        using var bothInside = new CountdownEvent(2);
        Task Reader() => Task.Run(() =>
        {
            gate.EnterReadLock();
            bothInside.Signal();
            Assert.True(bothInside.Wait(TimeSpan.FromSeconds(5)), "the second reader never got in");
            gate.ExitReadLock();
        });

        await Task.WhenAll(Reader(), Reader()).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Writer_WaitsForReadersToLeave_AndBlocksNewOnes()
    {
        var gate = new StripedReadWriteLock();
        var order = new List<string>();
        gate.EnterReadLock();

        Task writer = Task.Run(() =>
        {
            gate.EnterWriteLock();
            lock (order) { order.Add("writer"); }
            gate.ExitWriteLock();
        });
        await Task.Delay(100);
        Task reader = Task.Run(() =>
        {
            gate.EnterReadLock();
            lock (order) { order.Add("late reader"); }
            gate.ExitReadLock();
        });
        await Task.Delay(100);
        Assert.Empty(order);

        lock (order) { order.Add("first reader leaves"); }
        gate.ExitReadLock();
        await Task.WhenAll(writer, reader).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "first reader leaves", "writer", "late reader" }, order);
    }

    [Fact]
    public async Task WritersAndReaders_NeverOverlap()
    {
        var gate = new StripedReadWriteLock();
        int readers = 0;
        int writers = 0;
        int violations = 0;
        var tasks = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 20_000; i++)
            {
                if (t == 0 && i % 50 == 0)
                {
                    gate.EnterWriteLock();
                    if (Interlocked.Increment(ref writers) != 1 || Volatile.Read(ref readers) != 0) { Interlocked.Increment(ref violations); }
                    Interlocked.Decrement(ref writers);
                    gate.ExitWriteLock();
                    continue;
                }

                gate.EnterReadLock();
                Interlocked.Increment(ref readers);
                if (Volatile.Read(ref writers) != 0) { Interlocked.Increment(ref violations); }
                Interlocked.Decrement(ref readers);
                gate.ExitReadLock();
            }
        })).ToArray();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(0, violations);
    }

    [Fact]
    public void TakingTheWriteLockWhileReading_Throws()
    {
        var gate = new StripedReadWriteLock();
        gate.EnterReadLock();
        try
        {
            Assert.Throws<LockRecursionException>(gate.EnterWriteLock);
        }
        finally
        {
            gate.ExitReadLock();
        }
    }

    [Fact]
    public void TakingTheWriteLockTwice_Throws()
    {
        var gate = new StripedReadWriteLock();
        gate.EnterWriteLock();
        try
        {
            Assert.True(gate.IsWriteLockHeld);
            Assert.Throws<LockRecursionException>(gate.EnterWriteLock);
        }
        finally
        {
            gate.ExitWriteLock();
        }

        Assert.False(gate.IsWriteLockHeld);
    }
}
