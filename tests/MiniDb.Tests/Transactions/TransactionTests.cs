using MiniDb.Database;
using MiniDb.Storage;
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
    public void TransactionTouchingMorePagesThanPool_Throws_AndCanStillRollBack()
    {
        var store = NewStoreWithPages(3);
        var pool = new BufferPool(store, capacity: 2);
        using var wal = new WriteAheadLog(_walPath);
        var manager = new TransactionManager(store, pool, wal);

        using (Transaction tx = manager.Begin())
        {
            tx.WritePage(1, PageWith(1));
            tx.WritePage(2, PageWith(2));
            Assert.Throws<BufferPoolFullException>(() => tx.WritePage(3, PageWith(3)));
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
    }
}
