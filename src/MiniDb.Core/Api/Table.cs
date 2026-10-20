using MiniDb.Catalog;

namespace MiniDb.Api;

/// <summary>A table opened from a <see cref="MiniDatabase"/>. Writes run in the open transaction, or in their own if none is open.</summary>
public sealed class Table
{
    private readonly MiniDatabase _db;
    private readonly string _name;

    internal Table(MiniDatabase db, string name)
    {
        _db = db;
        _name = name;
    }

    public string Name => _name;

    /// <summary>Adds a row. Throws <see cref="Index.DuplicateKeyException"/> if the primary key exists.</summary>
    public void Insert(object?[] values)
    {
        _db.InWriteTransaction(() =>
        {
            TableInfo table = Load();
            TableInfo updated = RowStore.Insert(_db.Pool, table, values);
            if (updated.PrimaryKeyRoot != table.PrimaryKeyRoot || updated.HeapTail != table.HeapTail)
            {
                _db.Catalog.Replace(updated);
            }

            return true;
        });
    }

    /// <summary>The row with this primary key, or null.</summary>
    public object?[]? Get(long id) => RowStore.Get(_db.Pool, Load(), id);

    /// <summary>Every row, optionally filtered. Rows come back in heap order, not key order.</summary>
    public IEnumerable<object?[]> Scan(Func<object?[], bool>? where = null)
    {
        IEnumerable<object?[]> rows = RowStore.Scan(_db.Pool, Load());
        return where is null ? rows : rows.Where(where);
    }

    /// <summary>Removes the row with this primary key. Returns false if it does not exist.</summary>
    public bool Delete(long id) => _db.InWriteTransaction(() => RowStore.Delete(_db.Pool, Load(), id));

    private TableInfo Load() => _db.Catalog.Find(_name) ?? throw new TableNotFoundException(_name);
}
