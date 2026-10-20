using MiniDb.Api;
using MiniDb.Catalog;
using MiniDb.Index;
using MiniDb.Records;

namespace MiniDb.Query;

/// <summary>
/// Runs one parsed statement against a database. Chooses access paths through <see cref="Planner"/>. Rows are
/// filtered as <see cref="RowView"/>s: a comparison reads only its own column, and values are boxed only for output.
/// </summary>
internal static class SqlEngine
{
    public static QueryResult Run(MiniDatabase db, string sql) => Parser.Parse(sql) switch
    {
        CreateTableStatement create => CreateTable(db, create),
        InsertStatement insert => Insert(db, insert),
        SelectStatement select => Select(db, select),
        DeleteStatement delete => Delete(db, delete),
        _ => throw new InvalidOperationException("Unknown statement."),
    };

    private static QueryResult CreateTable(MiniDatabase db, CreateTableStatement statement)
    {
        if (statement.Columns.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != statement.Columns.Count)
        {
            throw new MiniDbException("A column name appears more than once in CREATE TABLE.");
        }

        var keys = statement.Columns.Select((column, index) => (column, index)).Where(c => c.column.IsPrimaryKey).ToList();
        if (keys.Count != 1)
        {
            throw new MiniDbException("A table needs exactly one PRIMARY KEY column.");
        }

        var columns = statement.Columns.Select(c => new ColumnDefinition(c.Name, c.Type, c.MaxLength)).ToList();
        db.CreateTable(new TableSchema(statement.Table, columns, keys[0].index));
        return QueryResult.Affected(0);
    }

    private static QueryResult Insert(MiniDatabase db, InsertStatement statement)
    {
        TableSchema schema = Load(db, statement.Table).Schema;
        if (statement.Values.Count != schema.Columns.Count)
        {
            throw new MiniDbException(
                $"Table '{schema.Name}' has {schema.Columns.Count} columns but {statement.Values.Count} values were given.");
        }

        object?[] values = schema.Columns.Select((column, i) => Coerce(column, statement.Values[i])).ToArray();
        db.GetTable(statement.Table).Insert(values);
        return QueryResult.Affected(1);
    }

    private static QueryResult Select(MiniDatabase db, SelectStatement statement)
    {
        TableInfo table = Load(db, statement.Table);
        if (statement.Where is { } where)
        {
            ColumnIndex(table.Schema, where.Column);
        }

        List<string> names = statement.Columns?.ToList() ?? table.Schema.Columns.Select(c => c.Name).ToList();
        int[] indexes = names.Select(name => ColumnIndex(table.Schema, name)).ToArray();
        List<object?[]> rows = FilterRows(db, table, statement.Where)
            .Take(statement.Limit ?? int.MaxValue)
            .Select(view => indexes.Select(i => view[i]).ToArray())
            .ToList();
        return new QueryResult(names, rows, 0);
    }

    private static QueryResult Delete(MiniDatabase db, DeleteStatement statement)
    {
        TableInfo table = Load(db, statement.Table);
        ColumnIndex(table.Schema, statement.Where.Column);
        int deleted = db.InWriteTransaction(() =>
        {
            // Materialize the matches first so the heap is not changed while it is being read.
            List<long> keys = FilterRows(db, table, statement.Where)
                .Select(view => view.GetInt64(table.Schema.PrimaryKeyIndex))
                .ToList();
            return keys.Count(key => RowStore.Delete(db.Pool, table, key));
        });
        return QueryResult.Affected(deleted);
    }

    private static IEnumerable<RowView> FilterRows(MiniDatabase db, TableInfo table, Condition? where)
    {
        IEnumerable<RowView> rows = Planner.Plan(table.Schema, where) switch
        {
            PointLookupPlan point => Single(RowStore.GetView(db.Pool, table, point.Key)),
            KeyRangePlan range => RangeRows(db, table, range),
            _ => RowStore.ScanRecords(db.Pool, table).Select(record => new RowView(table.Schema, record)),
        };
        if (where is not { } condition)
        {
            return rows;
        }

        return rows.Where(view => Matches(table.Schema, view, condition));
    }

    private static IEnumerable<RowView> RangeRows(MiniDatabase db, TableInfo table, KeyRangePlan range)
    {
        var tree = BPlusTree.Open(db.Pool, table.PrimaryKeyRoot);
        foreach ((long key, _) in tree.Range(range.From, range.To))
        {
            RowView? view = RowStore.GetView(db.Pool, table, key);
            if (view is not null)
            {
                yield return view.Value;
            }
        }
    }

    private static IEnumerable<RowView> Single(RowView? view) =>
        view is null ? Array.Empty<RowView>() : new[] { view.Value };

    private static bool Matches(TableSchema schema, RowView view, Condition condition)
    {
        int order = CompareColumn(schema, view, ColumnIndex(schema, condition.Column), condition.Value);
        return condition.Op switch
        {
            CompareOp.Equal => order == 0,
            CompareOp.Less => order < 0,
            CompareOp.LessOrEqual => order <= 0,
            CompareOp.Greater => order > 0,
            CompareOp.GreaterOrEqual => order >= 0,
            _ => false,
        };
    }

    /// <summary>Compares one column with a literal, reading only that column. Numbers compare across integer and decimal types.</summary>
    private static int CompareColumn(TableSchema schema, RowView view, int column, object literal) =>
        (schema.Columns[column].Type, literal) switch
        {
            (ColumnType.Int32, long l) => ((long)view.GetInt32(column)).CompareTo(l),
            (ColumnType.Int32, double d) => ((double)view.GetInt32(column)).CompareTo(d),
            (ColumnType.Int64, long l) => view.GetInt64(column).CompareTo(l),
            (ColumnType.Int64, double d) => ((double)view.GetInt64(column)).CompareTo(d),
            (ColumnType.Double, long l) => view.GetDouble(column).CompareTo((double)l),
            (ColumnType.Double, double d) => view.GetDouble(column).CompareTo(d),
            (ColumnType.Bool, bool b) => view.GetBool(column).CompareTo(b),
            (ColumnType.String, string s) => string.CompareOrdinal(view.GetString(column), s),
            _ => throw new MiniDbException(
                $"Cannot compare column '{schema.Columns[column].Name}' ({schema.Columns[column].Type}) with {Describe(literal)}."),
        };

    private static object Coerce(ColumnDefinition column, object literal) => (column.Type, literal) switch
    {
        (ColumnType.Int32, long value) when value is >= int.MinValue and <= int.MaxValue => (int)value,
        (ColumnType.Int64, long value) => value,
        (ColumnType.Double, double value) => value,
        (ColumnType.Double, long value) => (double)value,
        (ColumnType.Bool, bool value) => value,
        (ColumnType.String, string value) => value,
        _ => throw new MiniDbException($"Column '{column.Name}' expects {column.Type}, but got {Describe(literal)}."),
    };

    private static int ColumnIndex(TableSchema schema, string name)
    {
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            if (schema.Columns[i].Name == name)
            {
                return i;
            }
        }

        throw new MiniDbException($"Unknown column '{name}'.");
    }

    private static TableInfo Load(MiniDatabase db, string name) =>
        db.Catalog.Find(name) ?? throw new TableNotFoundException(name);

    private static string Describe(object value) => value switch
    {
        long or int => "an integer",
        double => "a decimal number",
        string => "text",
        bool => "a boolean",
        _ => value.GetType().Name,
    };
}
