using MiniDb.Database;
using MiniDb.Storage;
using MiniDb.Tests.Storage;
using MiniDb.Transactions;

namespace MiniDb.Tests.Transactions;

public class TransactionTests : IDisposable
{
    private readonly string _walPath = Path.Combine(Path.GetTempPath(), $"minidb-{Guid.NewGuid():N}.wal");

    public void Dispose()
    {
        File.Delete(_walPath);
    }

    private static MemoryPageStore NewStoreWithPages(int count)
    {
        var store = new MemoryPageStore();
        DatabaseFile.Create(store);
        for (int i = 0; i < count; i++)
        {
            store.AllocatePage();
        }

        return store;
    }

    private static byte[] PageWith(byte value)
    {
        var data = new byte[PageConstants.PageSize];
        data[0] = value;
        return data;
    }

    [Fact]
    public void CommittedWrite_IsInDataFileAfterCheckpoint()
    {
        var store = NewStoreWithPages(1);
        var pool = new BufferPool(store);
        using var wal = new WriteAheadLog(_walPath);
        var manager = new TransactionManager(store, pool, wal);

        using (Transaction tx = manager.Begin())
        {
            tx.WritePage(1, PageWith(0x55));
            tx.Commit();
        }
        manager.Checkpoint();

        var read = new byte[PageConstants.PageSize];
        store.ReadPage(1, read);
        Assert.Equal(0x55, read[0]);
    }

    [Fact]
    public void RolledBackWrite_LeavesPageUnchanged()
    {
        var store = NewStoreWithPages(1);
        var pool = new BufferPool(store);
        using var wal = new WriteAheadLog(_walPath);
        var manager = new TransactionManager(store, pool, wal);

        using (Transaction tx = manager.Begin())
        {
            tx.WritePage(1, PageWith(0x55));
            tx.Rollback();
        }
        manager.Checkpoint();

        var read = new byte[PageConstants.PageSize];
        store.ReadPage(1, read);
        Assert.Equal(0, read[0]);
    }

    [Fact]
    public void DisposeWithoutCommit_RollsBack()
    {
        var store = NewStoreWithPages(1);
        var pool = new BufferPool(store);
        using var wal = new WriteAheadLog(_walPath);
        var manager = new TransactionManager(store, pool, wal);

        using (Transaction tx = manager.Begin())
        {
            tx.WritePage(1, PageWith(0x55));
        }
        manager.Checkpoint();

        var read = new byte[PageConstants.PageSize];
        store.ReadPage(1, read);
        Assert.Equal(0, read[0]);
    }

    [Fact]
    public void TransactionTouchingMorePagesThanPool_Commits_AndSurvivesCheckpoint()
    {
        var store = NewStoreWithPages(6);
        var pool = new BufferPool(store, capacity: 2);
        using var wal = new WriteAheadLog(_walPath);
        var manager = new TransactionManager(store, pool, wal);

        using (Transaction tx = manager.Begin())
        {
            for (long page = 1; page <= 5; page++)
            {
                tx.WritePage(page, PageWith((byte)page));
            }

            tx.Commit();
        }

        manager.Checkpoint();
        var read = new byte[PageConstants.PageSize];
        for (long page = 1; page <= 5; page++)
        {
            store.ReadPage(page, read);
            Assert.Equal((byte)page, read[0]);
        }
    }

    [Fact]
    public void TransactionFarLargerThanPool_Throws_AndCanStillRollBack()
    {
        var store = NewStoreWithPages(40);
        var pool = new BufferPool(store, capacity: 2);
        using var wal = new WriteAheadLog(_walPath);
        var manager = new TransactionManager(store, pool, wal);

        using (Transaction tx = manager.Begin())
        {
            Assert.Throws<BufferPoolFullException>(() =>
            {
                for (long page = 1; page < 40; page++)
                {
                    tx.WritePage(page, PageWith(7));
                }
            });
        }

        using (Transaction next = manager.Begin())
        {
            next.WritePage(1, PageWith(9));
            next.Commit();
        }

        manager.Checkpoint();
        var read = new byte[PageConstants.PageSize];
        store.ReadPage(1, read);
        Assert.Equal(9, read[0]);
        store.ReadPage(2, read);
        Assert.Equal(0, read[0]);
    }

    [Fact]
    public void CommittedPages_AreFlushed_SoLaterReadsFitInSmallPool()
    {
        var store = NewStoreWithPages(4);
        var pool = new BufferPool(store, capacity: 2);
        using var wal = new WriteAheadLog(_walPath);
        var manager = new TransactionManager(store, pool, wal);

        using (Transaction tx = manager.Begin())
        {
            tx.WritePage(1, PageWith(1));
            tx.WritePage(2, PageWith(2));
            tx.Commit();
        }

        using (pool.Pin(3)) { }
        using (pool.Pin(4)) { }
    }

    [Fact]
    public void CommittedWrite_StaysInPool_UntilCheckpoint()
    {
        var store = NewStoreWithPages(1);
        var pool = new BufferPool(store);
        using var wal = new WriteAheadLog(_walPath);
        var manager = new TransactionManager(store, pool, wal);

        using (Transaction tx = manager.Begin())
        {
            tx.WritePage(1, PageWith(0x55));
            tx.Commit();
        }

        var beforeCheckpoint = new byte[PageConstants.PageSize];
        store.ReadPage(1, beforeCheckpoint);
        Assert.Equal(0, beforeCheckpoint[0]);
    }

    [Fact]
    public void RollbackAfterLaterWrite_KeepsEarlierCommit()
    {
        var store = NewStoreWithPages(1);
        var pool = new BufferPool(store);
        using var wal = new WriteAheadLog(_walPath);
        var manager = new TransactionManager(store, pool, wal);

        using (Transaction first = manager.Begin())
        {
            first.WritePage(1, PageWith(0x11));
            first.Commit();
        }
        using (Transaction second = manager.Begin())
        {
            second.WritePage(1, PageWith(0x22));
            second.Rollback();
        }

        using Transaction reader = manager.Begin();
        Assert.Equal(0x11, reader.ReadPage(1)[0]);
    }

    [Fact]
    public void AutocommitWrites_ReachDataFileOnlyAtCheckpoint()
    {
        var counting = new CountingPageStore(NewStoreWithPages(4));
        var pool = new BufferPool(counting);
        using var wal = new WriteAheadLog(_walPath);
        var manager = new TransactionManager(counting, pool, wal);

        for (int i = 0; i < 50; i++)
        {
            using Transaction tx = manager.Begin();
            tx.WritePage(1 + i % 3, PageWith((byte)i));
            tx.Commit();
        }

        Assert.Equal(0, counting.WriteCount);

        manager.Checkpoint();
        Assert.True(counting.WriteCount > 0);
    }

    [Fact]
    public void AutocommitWal_StaysSmall_WhenOnlyBytesChange()
    {
        var store = NewStoreWithPages(1);
        var pool = new BufferPool(store);
        using var wal = new WriteAheadLog(_walPath);
        var manager = new TransactionManager(store, pool, wal);

        for (int i = 0; i < 50; i++)
        {
            using Transaction tx = manager.Begin();
            tx.WritePage(1, PageWith((byte)(i + 1)));
            tx.Commit();
        }

        Assert.True(wal.Length < 50 * 200, $"WAL is {wal.Length} bytes");
    }
}
