using MiniDb.Storage;

namespace MiniDb.Database;

/// <summary>Creates, opens and updates the header page of a database file.</summary>
public static class DatabaseFile
{
    public const long HeaderPageId = 0;

    /// <summary>Initializes an empty store with a header page. The store must have no pages.</summary>
    public static void Create(IPageStore store)
    {
        if (store.PageCount != 0)
        {
            throw new InvalidOperationException("Create needs an empty page store.");
        }

        store.AllocatePage();
        Save(store, new DatabaseHeader(HeaderCodec.CurrentFormatVersion, PageCount: 1, CatalogRootPage: 0, FreeListHead: 0));
        store.Sync();
    }

    public static DatabaseHeader Open(IPageStore store)
    {
        if (store.PageCount == 0)
        {
            throw new InvalidDatabaseException("the file is empty");
        }

        var page = new byte[PageConstants.PageSize];
        store.ReadPage(HeaderPageId, page);
        return HeaderCodec.Read(page);
    }

    public static void Save(IPageStore store, DatabaseHeader header)
    {
        var page = new byte[PageConstants.PageSize];
        HeaderCodec.Write(header, page);
        store.WritePage(HeaderPageId, page);
    }
}
