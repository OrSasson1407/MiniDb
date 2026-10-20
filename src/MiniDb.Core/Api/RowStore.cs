using MiniDb.Catalog;
using MiniDb.Index;
using MiniDb.Records;
using MiniDb.Storage;

namespace MiniDb.Api;

/// <summary>
/// Row storage. Rows are appended to a chain of heap pages. The primary-key B+ tree maps each key to the
/// row's location. Every method here works on pages through the buffer pool, so writes must run inside a transaction.
/// </summary>
internal static class RowStore
{
    /// <summary>Adds a row and returns the table definition with its updated tree root and heap tail.</summary>
    public static TableInfo Insert(BufferPool pool, TableInfo table, object?[] values)
    {
        byte[] record = EncodeRow(table.Schema, values);
        long key = (long)values[table.Schema.PrimaryKeyIndex]!;
        var tree = BPlusTree.Open(pool, table.PrimaryKeyRoot);
        if (tree.Find(key) is not null)
        {
            throw new DuplicateKeyException(key);
        }

        (RowLocation location, long tail) = AppendRecord(pool, table.HeapTail, record);
        tree.Insert(key, location);
        return table with { PrimaryKeyRoot = tree.RootPage, HeapTail = tail };
    }

    public static object?[]? Get(BufferPool pool, TableInfo table, long key)
    {
        RowLocation? location = BPlusTree.Open(pool, table.PrimaryKeyRoot).Find(key);
        return location is { } found ? ReadRow(pool, table.Schema, found) : null;
    }

    /// <summary>Removes a row. The heap slot becomes a hole; the tree entry is removed. Returns false if the key is absent.</summary>
    public static bool Delete(BufferPool pool, TableInfo table, long key)
    {
        var tree = BPlusTree.Open(pool, table.PrimaryKeyRoot);
        RowLocation? location = tree.Find(key);
        if (location is not { } found)
        {
            return false;
        }

        using (PinnedPage page = pool.Pin(found.PageId))
        {
            SlottedPage.Open(page.Data).Delete(found.Slot);
            page.MarkDirty();
        }

        return tree.Delete(key);
    }

    /// <summary>Every live row, read page by page along the heap chain.</summary>
    public static IEnumerable<object?[]> Scan(BufferPool pool, TableInfo table)
    {
        long pageId = table.HeapRoot;
        while (pageId != 0)
        {
            (List<object?[]> rows, long next) = ReadHeapPage(pool, table.Schema, pageId);
            foreach (object?[] row in rows)
            {
                yield return row;
            }

            pageId = next;
        }
    }

    private static byte[] EncodeRow(TableSchema schema, object?[] values)
    {
        var record = new byte[RecordCodec.EncodedSize(schema, values)];
        RecordCodec.Encode(schema, values, record);
        return record;
    }

    private static (RowLocation Location, long Tail) AppendRecord(BufferPool pool, long tailPage, byte[] record)
    {
        using PinnedPage tail = pool.Pin(tailPage);
        SlottedPage tailSp = SlottedPage.Open(tail.Data);
        int slot = tailSp.Insert(record);
        if (slot >= 0)
        {
            tail.MarkDirty();
            return (new RowLocation(tailPage, slot), tailPage);
        }

        long newPage = pool.AllocatePage();
        tailSp.NextPage = newPage;
        tail.MarkDirty();

        using PinnedPage fresh = pool.Pin(newPage);
        SlottedPage freshSp = SlottedPage.Format(fresh.Data, newPage, PageType.Heap);
        int newSlot = freshSp.Insert(record);
        fresh.MarkDirty();
        return (new RowLocation(newPage, newSlot), newPage);
    }

    private static object?[]? ReadRow(BufferPool pool, TableSchema schema, RowLocation location)
    {
        using PinnedPage page = pool.Pin(location.PageId);
        ReadOnlySpan<byte> bytes = SlottedPage.Open(page.Data).Read(location.Slot);
        return bytes.IsEmpty ? null : RecordCodec.Decode(schema, bytes);
    }

    private static (List<object?[]> Rows, long Next) ReadHeapPage(BufferPool pool, TableSchema schema, long pageId)
    {
        using PinnedPage page = pool.Pin(pageId);
        SlottedPage sp = SlottedPage.Open(page.Data);
        var rows = new List<object?[]>();
        for (int slot = 0; slot < sp.SlotCount; slot++)
        {
            ReadOnlySpan<byte> bytes = sp.Read(slot);
            if (!bytes.IsEmpty)
            {
                rows.Add(RecordCodec.Decode(schema, bytes));
            }
        }

        return (rows, sp.NextPage);
    }
}
