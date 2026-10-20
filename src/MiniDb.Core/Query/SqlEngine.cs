using MiniDb.Api;
using MiniDb.Catalog;
using MiniDb.Index;
using MiniDb.Records;

namespace MiniDb.Query;

/// <summary>Runs one parsed statement against a database. Chooses access paths through <see cref="Planner"/>.</summary>
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
            .Select(row => indexes.Select(i => row[i]).ToArray())
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
                .Select(row => (long)row[table.Schema.PrimaryKeyIndex]!)
                .ToList();
            return keys.Count(key => RowStore.Delete(db.Pool, table, key));
        });
        return QueryResult.Affected(deleted);
    }

    private static IEnumerable<object?[]> FilterRows(MiniDatabase db, TableInfo table, Condition? where)
    {
        IEnumerable<object?[]> rows = Planner.Plan(table.Schema, where) switch
        {
            PointLookupPlan point => Single(RowStore.Get(db.Pool, table, point.Key)),
            KeyRangePlan range => RangeRows(db, table, range),
            _ => RowStore.Scan(db.Pool, table),
        };
        if (where is not { } condition)
        {
            return rows;
        }

        return rows.Where(row => Matches(table.Schema, row, condition));
    }

    private static IEnumerable<object?[]> RangeRows(MiniDatabase db, TableInfo table, KeyRangePlan range)
    {
        var tree = BPlusTree.Open(db.Pool, table.PrimaryKeyRoot);
        foreach ((long key, _) in tree.Range(range.From, range.To))
        {
            object?[]? row = RowStore.Get(db.Pool, table, key);
            if (row is not null)
            {
                yield return row;
            }
        }
    }

    private static IEnumerable<object?[]> Single(object?[]? row) =>
        row is null ? Array.Empty<object?[]>() : new[] { row };

    private static bool Matches(TableSchema schema, object?[] row, Condition condition)
    {
        int order = CompareValues(row[ColumnIndex(schema, condition.Column)]!, condition.Value);
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

    private static int CompareValues(object left, object right)
    {
        if (left is long a && right is long b)
        {
            return a.CompareTo(b);
        }

        if (IsNumber(left) && IsNumber(right))
        {
            return ToDouble(left).CompareTo(ToDouble(right));
        }

        if (left is string s && right is string t)
        {
            return string.CompareOrdinal(s, t);
        }

        if (left is bool x && right is bool y)
        {
            return x.CompareTo(y);
        }

        throw new MiniDbException($"Cannot compare {Describe(left)} with {Describe(right)}.");
    }

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

    private static bool IsNumber(object value) => value is int or long or double;

    private static double ToDouble(object value) => value switch
    {
        int i => i,
        long l => l,
        double d => d,
        _ => throw new InvalidOperationException("Not a number."),
    };

    private static string Describe(object value) => value switch
    {
        long or int => "an integer",
        double => "a decimal number",
        string => "text",
        bool => "a boolean",
        _ => value.GetType().Name,
    };
}
