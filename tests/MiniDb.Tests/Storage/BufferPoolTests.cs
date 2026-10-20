using MiniDb.Storage;

namespace MiniDb.Tests.Storage;

public class BufferPoolTests
{
    [Fact]
    public void PinningSamePageTwice_ReadsFromStoreOnlyOnce()
    {
        var counting = new CountingPageStore(new MemoryPageStore());
        long id = counting.AllocatePage();
        using var pool = new BufferPool(counting, capacity: 4);

        using (pool.Pin(id)) { }
        using (pool.Pin(id)) { }

        Assert.Equal(1, counting.ReadCount);
    }

    [Fact]
    public void PinningMoreDistinctPagesThanCapacity_WhileAllPinned_Throws()
    {
        var store = new MemoryPageStore();
        var pages = Enumerable.Range(0, 5).Select(_ => store.AllocatePage()).ToList();
        using var pool = new BufferPool(store, capacity: 4);
        var held = pages.Take(4).Select(pool.Pin).ToList();

        Assert.Throws<BufferPoolFullException>(() => pool.Pin(pages[4]));

        held.ForEach(p => p.Dispose());
    }

    [Fact]
    public void DirtyPage_IsNotEvicted_AndNotWrittenToStore()
    {
        var counting = new CountingPageStore(new MemoryPageStore());
        long first = counting.AllocatePage();
        long second = counting.AllocatePage();
        using var pool = new BufferPool(counting, capacity: 1);
        pool.BeginTracking();

        using (var page = pool.Pin(first))
        {
            page.Data[0] = 0x55;
            page.MarkDirty();
        }

        Assert.Throws<BufferPoolFullException>(() => pool.Pin(second));
        Assert.Equal(0, counting.WriteCount);
    }

    [Fact]
    public void FlushAll_WritesDirtyPagesToStore()
    {
        var store = new MemoryPageStore();
        long id = store.AllocatePage();
        using var pool = new BufferPool(store, capacity: 4);

        using (var page = pool.Pin(id))
        {
            page.Data[0] = 0x55;
            page.MarkDirty();
        }
        pool.FlushAll();

        var read = new byte[PageConstants.PageSize];
        store.ReadPage(id, read);
        Assert.Equal(0x55, read[0]);
    }

    [Fact]
    public void Discard_DropsUnflushedChanges()
    {
        var store = new MemoryPageStore();
        long id = store.AllocatePage();
        using var pool = new BufferPool(store, capacity: 4);

        using (var page = pool.Pin(id))
        {
            page.Data[0] = 0x55;
            page.MarkDirty();
        }
        pool.Discard(id);

        using var reread = pool.Pin(id);
        Assert.Equal(0, reread.Data[0]);
    }

    [Fact]
    public void FailedLoad_DoesNotShrinkThePool()
    {
        var store = new MemoryPageStore();
        store.AllocatePage();
        using var pool = new BufferPool(store, capacity: 1);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Assert.Throws<PageOutOfRangeException>(() => pool.Pin(99));
        }

        using (pool.Pin(0)) { }
    }

    [Fact]
    public void SequentialScan_DoesNotEvictHotPages()
    {
        var counting = new CountingPageStore(new MemoryPageStore());
        for (int i = 0; i < 20; i++)
        {
            counting.AllocatePage();
        }

        using var pool = new BufferPool(counting, capacity: 4);
        using (pool.Pin(0)) { }
        for (long id = 1; id < 20; id++)
        {
            using (pool.Pin(id, sequential: true)) { }
        }

        int readsBefore = counting.ReadCount;
        using (pool.Pin(0)) { }

        Assert.Equal(readsBefore, counting.ReadCount);
    }

    [Fact]
    public void Prefetch_LoadsPageWithoutPinningIt()
    {
        var counting = new CountingPageStore(new MemoryPageStore());
        counting.AllocatePage();
        counting.AllocatePage();
        using var pool = new BufferPool(counting, capacity: 4);

        pool.Prefetch(1);
        int readsAfterPrefetch = counting.ReadCount;
        using (pool.Pin(1, sequential: true)) { }

        Assert.Equal(1, readsAfterPrefetch);
        Assert.Equal(readsAfterPrefetch, counting.ReadCount);
    }
}
