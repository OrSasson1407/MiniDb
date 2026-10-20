using MiniDb.Records;

namespace MiniDb.Tests.Records;

public class SlottedPageTests
{
    [Fact]
    public void InsertReadDelete_WorksPerSlot()
    {
        var bytes = new byte[4096];
        var page = SlottedPage.Format(bytes, pageId: 1, PageType.Heap);
        int a = page.Insert("a"u8);
        int b = page.Insert("bb"u8);
        int c = page.Insert("ccc"u8);

        Assert.Equal("bb"u8.ToArray(), page.Read(b).ToArray());
        Assert.True(page.Delete(b));
        Assert.Empty(page.Read(b).ToArray());
        Assert.False(page.Delete(b));
        Assert.Equal("a"u8.ToArray(), page.Read(a).ToArray());
        Assert.Equal("ccc"u8.ToArray(), page.Read(c).ToArray());
    }

    [Fact]
    public void Insert_UntilFull_ReturnsMinusOne()
    {
        var bytes = new byte[4096];
        var page = SlottedPage.Format(bytes, pageId: 1, PageType.Heap);
        var record = new byte[100];

        int inserted = 0;
        while (page.Insert(record) >= 0)
        {
            inserted++;
        }

        Assert.Equal(39, inserted);
    }

    [Fact]
    public void Read_OutOfRangeSlot_Throws()
    {
        var bytes = new byte[4096];
        SlottedPage.Format(bytes, pageId: 1, PageType.Heap);

        Assert.Throws<ArgumentOutOfRangeException>(() => SlottedPage.Open(bytes).Read(0));
    }
}
