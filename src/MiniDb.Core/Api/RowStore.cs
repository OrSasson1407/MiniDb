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
        // No separate lookup: a duplicate key makes tree.Insert throw, and the enclosing
        // transaction rolls back the heap row that was already appended.
        var tree = BPlusTree.Open(pool, table.PrimaryKeyRoot);
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

    /// <summary>Every live row, read page by page along the heap chain. Holds at most one page pinned.</summary>
    public static IEnumerable<object?[]> Scan(BufferPool pool, TableInfo table)
    {
        long pageId = table.HeapRoot;
        while (pageId != 0)
        {
            using PinnedPage page = pool.Pin(pageId);
            int slots = SlotCount(page.Data);
            for (int slot = 0; slot < slots; slot++)
            {
                object?[]? row = ReadLiveSlot(page.Data, table.Schema, slot);
                if (row is not null)
                {
                    yield return row;
                }
            }

            pageId = NextPage(page.Data);
        }
    }

    /// <summary>The row with this key as a view that owns a copy of its record, or null.</summary>
    public static RowView? GetView(BufferPool pool, TableInfo table, long key)
    {
        RowLocation? location = BPlusTree.Open(pool, table.PrimaryKeyRoot).Find(key);
        if (location is not { } found)
        {
            return null;
        }

        using PinnedPage page = pool.Pin(found.PageId);
        byte[]? record = ReadLiveRecord(page.Data, found.Slot);
        return record is null ? null : new RowView(table.Schema, record);
    }

    /// <summary>Copies of every live record, in heap order. Holds at most one page pinned.</summary>
    public static IEnumerable<byte[]> ScanRecords(BufferPool pool, TableInfo table)
    {
        long pageId = table.HeapRoot;
        while (pageId != 0)
        {
            using PinnedPage page = pool.Pin(pageId);
            int slots = SlotCount(page.Data);
            for (int slot = 0; slot < slots; slot++)
            {
                byte[]? record = ReadLiveRecord(page.Data, slot);
                if (record is not null)
                {
                    yield return record;
                }
            }

            pageId = NextPage(page.Data);
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

    private static int SlotCount(Span<byte> data) => SlottedPage.Open(data).SlotCount;

    private static long NextPage(Span<byte> data) => SlottedPage.Open(data).NextPage;

    private static byte[]? ReadLiveRecord(Span<byte> data, int slot)
    {
        ReadOnlySpan<byte> bytes = SlottedPage.Open(data).Read(slot);
        return bytes.IsEmpty ? null : bytes.ToArray();
    }

    private static object?[]? ReadLiveSlot(Span<byte> data, TableSchema schema, int slot)
    {
        ReadOnlySpan<byte> bytes = SlottedPage.Open(data).Read(slot);
        return bytes.IsEmpty ? null : RecordCodec.Decode(schema, bytes);
    }
}
