using MiniDb.Database;
using MiniDb.Storage;
using MiniDb.Transactions;

namespace MiniDb.Tests.Transactions;

public class RecoveryTests : IDisposable
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

    private static byte FirstByteOf(IPageStore store, long pageId)
    {
        var read = new byte[PageConstants.PageSize];
        store.ReadPage(pageId, read);
        return read[0];
    }

    [Fact]
    public void UncommittedPageImage_IsIgnored()
    {
        var store = NewStoreWithPages(2);
        using (var wal = new WriteAheadLog(_walPath))
        {
            wal.Append(new WalFrame(1, WalFrameType.PageImage, 1, PageWith(0x77)));
            wal.Sync();
        }

        using (var reopened = new WriteAheadLog(_walPath))
        {
            Recovery.Run(store, reopened);
        }

        Assert.Equal(0, FirstByteOf(store, 1));
    }

    [Fact]
    public void CommittedPageImage_IsApplied_AndLogIsEmptied()
    {
        var store = NewStoreWithPages(2);
        using (var wal = new WriteAheadLog(_walPath))
        {
            wal.Append(new WalFrame(2, WalFrameType.PageImage, 1, PageWith(0x77)));
            wal.Append(new WalFrame(2, WalFrameType.Commit, 0, Array.Empty<byte>()));
            wal.Sync();
        }

        using var reopened = new WriteAheadLog(_walPath);
        Recovery.Run(store, reopened);

        Assert.Equal(0x77, FirstByteOf(store, 1));
        Assert.Empty(reopened.ReadAll());
    }

    [Fact]
    public void TornTailAfterCommit_KeepsCommittedWork()
    {
        var store = NewStoreWithPages(2);
        using (var wal = new WriteAheadLog(_walPath))
        {
            wal.Append(new WalFrame(3, WalFrameType.PageImage, 1, PageWith(0x33)));
            wal.Append(new WalFrame(3, WalFrameType.Commit, 0, Array.Empty<byte>()));
        }
        using (var raw = new FileStream(_walPath, FileMode.Append, FileAccess.Write))
        {
            raw.Write(new byte[] { 9, 9, 9 });
        }

        using (var reopened = new WriteAheadLog(_walPath))
        {
            Recovery.Run(store, reopened);
        }

        Assert.Equal(0x33, FirstByteOf(store, 1));
    }

    [Fact]
    public void CrashBeforeCheckpoint_ReplaysCommittedTransaction()
    {
        var store = NewStoreWithPages(2);
        var pool = new BufferPool(store);
        var wal = new WriteAheadLog(_walPath);
        var manager = new TransactionManager(store, pool, wal);
        using (Transaction tx = manager.Begin())
        {
            tx.WritePage(1, PageWith(0x55));
            tx.Commit();
        }

        // Simulated crash: the pool's unflushed contents are lost; only the store and the WAL survive.
        wal.Dispose();
        using (var reopened = new WriteAheadLog(_walPath))
        {
            Recovery.Run(store, reopened);
        }

        Assert.Equal(0x55, FirstByteOf(store, 1));
    }
}
