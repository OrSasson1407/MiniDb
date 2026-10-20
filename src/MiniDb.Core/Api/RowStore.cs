using MiniDb.Catalog;
using MiniDb.Index;
using MiniDb.Records;
using MiniDb.Storage;

namespace MiniDb.Api;

/// <summary>
/// Row storage for clustered tables. The primary-key <see cref="ClusteredTree"/> holds each row inside its leaf.
/// Each secondary index maps (indexed value, primary key) to the row's key; a lookup through an index goes back to the
/// primary tree. Every method here works on pages through the buffer pool, so writes must run inside a transaction.
/// </summary>
internal static class RowStore
{
    /// <summary>Adds a row and returns the table definition with its updated tree roots.</summary>
    public static TableInfo Insert(BufferPool pool, TableInfo table, object?[] values)
    {
        byte[] record = EncodeRow(table.Schema, values);
        long key = (long)values[table.Schema.PrimaryKeyIndex]!;
        var primary = ClusteredTree.Open(pool, table.PrimaryKeyRoot);
        primary.Insert(key, record);

        Dictionary<string, long>? indexRoots = null;
        foreach ((string column, long root) in table.SecondaryIndexRoots)
        {
            int index = ColumnIndex(table.Schema, column);
            var secondary = BPlusTree.Open(pool, root);
            secondary.Insert(new TreeKey(IndexValue(values[index]!), key), default);
            if (secondary.RootPage != root)
            {
                indexRoots ??= new Dictionary<string, long>(table.SecondaryIndexRoots);
                indexRoots[column] = secondary.RootPage;
            }
        }

        if (indexRoots is null && primary.RootPage == table.PrimaryKeyRoot)
        {
            return table;
        }

        return table with { PrimaryKeyRoot = primary.RootPage, SecondaryIndexRoots = indexRoots ?? table.SecondaryIndexRoots };
    }

    public static object?[]? Get(BufferPool pool, TableInfo table, long key)
    {
        byte[]? record = ClusteredTree.Open(pool, table.PrimaryKeyRoot).Find(key);
        return record is null ? null : RecordCodec.Decode(table.Schema, record);
    }

    /// <summary>The row with this key as a view that owns a copy of its record, or null.</summary>
    public static RowView? GetView(BufferPool pool, TableInfo table, long key)
    {
        byte[]? record = ClusteredTree.Open(pool, table.PrimaryKeyRoot).Find(key);
        return record is null ? null : new RowView(table.Schema, record);
    }

    /// <summary>Removes a row from the primary tree and from every secondary index. Returns false if the key is absent.</summary>
    public static bool Delete(BufferPool pool, TableInfo table, long key)
    {
        var primary = ClusteredTree.Open(pool, table.PrimaryKeyRoot);
        byte[]? record = primary.Find(key);
        if (record is null)
        {
            return false;
        }

        var row = new RowView(table.Schema, record);
        foreach ((string column, long root) in table.SecondaryIndexRoots)
        {
            BPlusTree.Open(pool, root).Delete(new TreeKey(IndexValue(row, table.Schema, ColumnIndex(table.Schema, column)), key));
        }

        return primary.Delete(key);
    }

    /// <summary>Builds a secondary index on an integer column from the rows already stored.</summary>
    public static TableInfo AddIndex(BufferPool pool, TableInfo table, int column)
    {
        var index = BPlusTree.Create(pool);
        foreach (byte[] record in ScanRecords(pool, table))
        {
            var view = new RowView(table.Schema, record);
            long key = view.GetInt64(table.Schema.PrimaryKeyIndex);
            index.Insert(new TreeKey(IndexValue(view, table.Schema, column), key), default);
        }

        var roots = new Dictionary<string, long>(table.SecondaryIndexRoots)
        {
            [table.Schema.Columns[column].Name] = index.RootPage,
        };
        return table with { SecondaryIndexRoots = roots };
    }

    /// <summary>Every row in primary-key order. Holds at most one leaf pinned at a time.</summary>
    public static IEnumerable<object?[]> Scan(BufferPool pool, TableInfo table) =>
        ScanRecords(pool, table).Select(record => RecordCodec.Decode(table.Schema, record));

    /// <summary>Copies of every record in primary-key order.</summary>
    public static IEnumerable<byte[]> ScanRecords(BufferPool pool, TableInfo table) =>
        ClusteredTree.Open(pool, table.PrimaryKeyRoot).Range(long.MinValue, long.MaxValue).Select(cell => cell.Record);

    /// <summary>The value an index stores for a row. Only integer columns can be indexed.</summary>
    public static long IndexValue(object value) => value switch
    {
        int i => i,
        long l => l,
        _ => throw new InvalidOperationException("Only integer columns can be indexed."),
    };

    private static long IndexValue(RowView view, TableSchema schema, int column) =>
        schema.Columns[column].Type == ColumnType.Int32 ? view.GetInt32(column) : view.GetInt64(column);

    private static int ColumnIndex(TableSchema schema, string name)
    {
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            if (schema.Columns[i].Name == name)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Column '{name}' is not in the schema.");
    }

    private static byte[] EncodeRow(TableSchema schema, object?[] values)
    {
        var record = new byte[RecordCodec.EncodedSize(schema, values)];
        RecordCodec.Encode(schema, values, record);
        return record;
    }
}
