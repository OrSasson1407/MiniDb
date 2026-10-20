using MiniDb.Catalog;
using MiniDb.Records;

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
    public void Insert(object?[] values) =>
        _db.InWriteTransaction((Table: this, Values: values), static state => state.Table.InsertRow(state.Values));

    /// <summary>The row with this primary key, or null.</summary>
    public object?[]? Get(long id) =>
        _db.ReadLocked((Table: this, Id: id), static state => RowStore.Get(state.Table._db.Pool, state.Table.Load(), state.Id));

    /// <summary>Every row, optionally filtered. Rows come back in heap order, not key order.</summary>
    public IEnumerable<object?[]> Scan(Func<object?[], bool>? where = null)
    {
        IEnumerable<object?[]> rows = _db.ReadLockedSequence(() => RowStore.Scan(_db.Pool, Load()));
        return where is null ? rows : rows.Where(where);
    }

    /// <summary>The row with this primary key as a view that reads columns on demand, or null.</summary>
    public RowView? GetView(long id) =>
        _db.ReadLocked((Table: this, Id: id), static state => RowStore.GetView(state.Table._db.Pool, state.Table.Load(), state.Id));

    /// <summary>Every row as a view, optionally filtered. A filter reads only the columns it asks for.</summary>
    public IEnumerable<RowView> ScanViews(Func<RowView, bool>? where = null)
    {
        IEnumerable<RowView> views = _db.ReadLockedSequence(() =>
        {
            TableInfo table = Load();
            return RowStore.ScanRecords(_db.Pool, table).Select(record => new RowView(table.Schema, record));
        });
        return where is null ? views : views.Where(where);
    }

    /// <summary>Removes the row with this primary key. Returns false if it does not exist.</summary>
    public bool Delete(long id) => _db.InWriteTransaction(() => RowStore.Delete(_db.Pool, Load(), id));

    private bool InsertRow(object?[] values)
    {
        TableInfo table = Load();
        TableInfo updated = RowStore.Insert(_db.Pool, table, values);
        if (!ReferenceEquals(updated, table))
        {
            _db.Catalog.Replace(updated);
        }

        return true;
    }

    private TableInfo Load() => _db.Catalog.Find(_name) ?? throw new TableNotFoundException(_name);
}
