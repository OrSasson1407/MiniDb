using MiniDb.Database;
using MiniDb.Storage;

namespace MiniDb.Tests.Database;

public class DatabaseFileTests
{
    [Fact]
    public void Create_ThenOpen_ReturnsFreshHeader()
    {
        var store = new MemoryPageStore();

        DatabaseFile.Create(store);
        DatabaseHeader header = DatabaseFile.Open(store);

        Assert.Equal(HeaderCodec.CurrentFormatVersion, header.FormatVersion);
        Assert.Equal(1, header.PageCount);
    }

    [Fact]
    public void Open_EmptyStore_Throws()
    {
        var store = new MemoryPageStore();

        Assert.Throws<InvalidDatabaseException>(() => DatabaseFile.Open(store));
    }

    [Fact]
    public void Open_PageZeroNotAMiniDbHeader_Throws()
    {
        var store = new MemoryPageStore();
        store.AllocatePage();

        Assert.Throws<InvalidDatabaseException>(() => DatabaseFile.Open(store));
    }

    [Fact]
    public void Save_ThenOpen_ReturnsUpdatedHeader()
    {
        var store = new MemoryPageStore();
        DatabaseFile.Create(store);

        DatabaseFile.Save(store, new DatabaseHeader(HeaderCodec.CurrentFormatVersion, PageCount: 4, CatalogRootPage: 2, FreeListHead: 0));

        Assert.Equal(4, DatabaseFile.Open(store).PageCount);
    }
}
