using MiniDb.Storage;

namespace MiniDb.Tests.Storage;

public class PageChecksumTests
{
    [Fact]
    public void StampedPage_Verifies()
    {
        var page = new byte[PageConstants.PageSize];
        page[5] = 7;
        PageChecksum.Stamp(page);

        PageChecksum.Verify(page, 1);
    }

    [Fact]
    public void NeverWrittenPage_Verifies()
    {
        PageChecksum.Verify(new byte[PageConstants.PageSize], 1);
    }

    [Fact]
    public void DamagedPage_FailsVerification()
    {
        var page = new byte[PageConstants.PageSize];
        page[5] = 7;
        PageChecksum.Stamp(page);
        page[100] ^= 1;

        Assert.Throws<CorruptPageException>(() => PageChecksum.Verify(page, 1));
    }

    [Fact]
    public void BufferPool_DetectsDamageWhenPageIsReloaded()
    {
        var store = new MemoryPageStore();
        long id = store.AllocatePage();
        var pool = new BufferPool(store);
        using (PinnedPage page = pool.Pin(id))
        {
            page.Data[10] = 5;
            page.MarkDirty();
        }
        pool.FlushAll();

        var raw = new byte[PageConstants.PageSize];
        store.ReadPage(id, raw);
        raw[10] ^= 0xFF;
        store.WritePage(id, raw);

        var reloaded = new BufferPool(store);
        Assert.Throws<CorruptPageException>(() => reloaded.Pin(id));
    }
}
