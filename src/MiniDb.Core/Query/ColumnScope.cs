using MiniDb.Catalog;
using MiniDb.Records;

namespace MiniDb.Query;

/// <summary>A column of one table in the FROM clause: the table's position there, the column's position, and its type.</summary>
internal readonly record struct ColumnRef(int Table, int Column, ColumnType Type, string Name);

/// <summary>The tables a statement reads, in FROM order. Resolves plain and <c>table.column</c> names.</summary>
internal sealed class ColumnScope
{
    private readonly IReadOnlyList<TableInfo> _tables;

    public ColumnScope(IReadOnlyList<TableInfo> tables)
    {
        _tables = tables;
    }

    public int TableCount => _tables.Count;

    public TableInfo Table(int position) => _tables[position];

    /// <summary>Finds a column. An unqualified name must match exactly one table.</summary>
    public ColumnRef Resolve(string name)
    {
        int dot = name.IndexOf('.');
        if (dot >= 0)
        {
            string table = name[..dot];
            string column = name[(dot + 1)..];
            for (int position = 0; position < _tables.Count; position++)
            {
                if (_tables[position].Schema.Name == table)
                {
                    return Find(position, column, name);
                }
            }

            throw new MiniDbException($"Unknown table '{table}' in column '{name}'.");
        }

        ColumnRef? found = null;
        for (int position = 0; position < _tables.Count; position++)
        {
            if (IndexOf(_tables[position].Schema, name) < 0)
            {
                continue;
            }

            if (found is not null)
            {
                throw new MiniDbException($"Column '{name}' is in more than one table; qualify it with the table name.");
            }

            found = Find(position, name, name);
        }

        return found ?? throw new MiniDbException($"Unknown column '{name}'.");
    }

    /// <summary>Column position within a table by name, or -1.</summary>
    public static int IndexOf(TableSchema schema, string name)
    {
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            if (schema.Columns[i].Name == name)
            {
                return i;
            }
        }

        return -1;
    }

    private ColumnRef Find(int tablePosition, string column, string written)
    {
        TableSchema schema = _tables[tablePosition].Schema;
        int index = IndexOf(schema, column);
        if (index < 0)
        {
            throw new MiniDbException($"Unknown column '{written}'.");
        }

        return new ColumnRef(tablePosition, index, schema.Columns[index].Type, schema.Columns[index].Name);
    }
}
