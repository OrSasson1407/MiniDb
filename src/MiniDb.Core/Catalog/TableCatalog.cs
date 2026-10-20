using MiniDb.Records;
using MiniDb.Storage;

namespace MiniDb.Catalog;

/// <summary>
/// Table definitions, stored one per slot in a chain of slotted pages that starts at <c>rootPage</c>.
/// Every page of the chain has type <see cref="PageType.Catalog"/>.
/// </summary>
public sealed class TableCatalog
{
    private readonly IBufferPool _pool;
    private readonly long _rootPage;

    public TableCatalog(IBufferPool pool, long rootPage)
    {
        _pool = pool;
        _rootPage = rootPage;
    }

    /// <summary>Allocates and formats an empty catalog page and returns its id.</summary>
    public static long CreateRoot(IBufferPool pool)
    {
        long pageId = pool.AllocatePage();
        using PinnedPage page = pool.Pin(pageId);
        SlottedPage.Format(page.Data, pageId, PageType.Catalog);
        page.MarkDirty();
        return pageId;
    }

    public void Create(TableInfo table)
    {
        if (Find(table.Schema.Name) is not null)
        {
            throw new TableAlreadyExistsException(table.Schema.Name);
        }

        byte[] record = TableInfoCodec.Encode(table);
        if (record.Length > SlottedPage.MaxRecordSize)
        {
            throw new RecordTooLargeException(record.Length);
        }

        AppendRecord(record);
    }

    /// <summary>Replaces the stored definition of the table with the same name.</summary>
    public void Replace(TableInfo table)
    {
        long pageId = _rootPage;
        while (pageId != 0)
        {
            (bool replaced, long next) = TryReplaceOnPage(pageId, table);
            if (replaced)
            {
                return;
            }

            pageId = next;
        }

        throw new InvalidOperationException($"Table '{table.Schema.Name}' is not in the catalog.");
    }

    public TableInfo? Find(string name) => All().FirstOrDefault(t => t.Schema.Name == name);

    public IEnumerable<TableInfo> All()
    {
        long pageId = _rootPage;
        while (pageId != 0)
        {
            (List<TableInfo> tables, long next) = ReadPage(pageId);
            foreach (TableInfo table in tables)
            {
                yield return table;
            }

            pageId = next;
        }
    }

    private (bool Replaced, long Next) TryReplaceOnPage(long pageId, TableInfo table)
    {
        using PinnedPage page = _pool.Pin(pageId);
        SlottedPage sp = SlottedPage.Open(page.Data);
        for (int slot = 0; slot < sp.SlotCount; slot++)
        {
            ReadOnlySpan<byte> bytes = sp.Read(slot);
            if (!bytes.IsEmpty && TableInfoCodec.Decode(bytes).Schema.Name == table.Schema.Name)
            {
                byte[] record = TableInfoCodec.Encode(table);
                if (record.Length <= bytes.Length)
                {
                    sp.Overwrite(slot, record);
                }
                else
                {
                    sp.Delete(slot);
                    AppendRecord(record);
                }

                page.MarkDirty();
                return (true, 0);
            }
        }

        return (false, sp.NextPage);
    }

    private (List<TableInfo> Tables, long Next) ReadPage(long pageId)
    {
        using PinnedPage page = _pool.Pin(pageId);
        SlottedPage sp = SlottedPage.Open(page.Data);
        var tables = new List<TableInfo>();
        for (int slot = 0; slot < sp.SlotCount; slot++)
        {
            ReadOnlySpan<byte> bytes = sp.Read(slot);
            if (!bytes.IsEmpty)
            {
                tables.Add(TableInfoCodec.Decode(bytes));
            }
        }

        return (tables, sp.NextPage);
    }

    private void AppendRecord(byte[] record)
    {
        long pageId = _rootPage;
        while (true)
        {
            using PinnedPage page = _pool.Pin(pageId);
            SlottedPage sp = SlottedPage.Open(page.Data);
            if (sp.Insert(record) >= 0)
            {
                page.MarkDirty();
                return;
            }

            if (sp.NextPage != 0)
            {
                pageId = sp.NextPage;
                continue;
            }

            pageId = LinkNewPage(page, sp);
        }
    }

    private long LinkNewPage(PinnedPage last, SlottedPage lastSp)
    {
        long next = _pool.AllocatePage();
        lastSp.NextPage = next;
        last.MarkDirty();

        using PinnedPage page = _pool.Pin(next);
        SlottedPage.Format(page.Data, next, PageType.Catalog);
        page.MarkDirty();
        return next;
    }
}
