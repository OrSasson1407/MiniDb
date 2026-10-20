using MiniDb.Api;
using MiniDb.Catalog;
using MiniDb.Records;

namespace MiniDb.Query;

/// <summary>
/// Runs one parsed statement against a database. SELECT runs under the shared lock and is answered by
/// <see cref="SelectExecutor"/>. Every other statement runs in a write transaction, and a statement that fails
/// halfway rolls back all of its changes.
/// </summary>
internal static class SqlEngine
{
    public static QueryResult Execute(MiniDatabase db, string sql)
    {
        Statement statement = Parser.Parse(sql);
        if (statement is SelectStatement select)
        {
            return db.ReadLocked(() => SelectExecutor.Run(db, select));
        }

        return db.InWriteTransaction(() => statement switch
        {
            CreateTableStatement create => CreateTable(db, create),
            CreateIndexStatement createIndex => CreateIndex(db, createIndex),
            InsertStatement insert => Insert(db, insert),
            UpdateStatement update => UpdateExecutor.Run(db, update),
            DeleteStatement delete => Delete(db, delete),
            _ => throw new InvalidOperationException("Unknown statement."),
        });
    }

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

    private static QueryResult Delete(MiniDatabase db, DeleteStatement statement)
    {
        TableInfo table = Load(db, statement.Table);
        Func<RowView, bool> filter = Predicates.ForSingleTable(new ColumnScope(new[] { table }), statement.Where);
        // Materialize the matches first so the tree is not changed while it is being read.
        List<long> keys = AccessPaths.Rows(db, table, AccessPaths.ChoosePlan(db, table, statement.Where))
            .Where(filter)
            .Select(row => row.GetInt64(table.Schema.PrimaryKeyIndex))
            .ToList();
        return QueryResult.Affected(keys.Count(key => RowStore.Delete(db.Pool, table, key)));
    }

    private static QueryResult CreateIndex(MiniDatabase db, CreateIndexStatement statement)
    {
        TableInfo table = Load(db, statement.Table);
        int column = ColumnScope.IndexOf(table.Schema, statement.Column);
        if (column < 0)
        {
            throw new MiniDbException($"Unknown column '{statement.Column}'.");
        }

        ColumnType type = table.Schema.Columns[column].Type;
        if (type is not (ColumnType.Int32 or ColumnType.Int64 or ColumnType.String))
        {
            throw new MiniDbException($"Column '{statement.Column}' cannot be indexed: only INT, BIGINT and VARCHAR columns can be indexed.");
        }

        if (table.SecondaryIndexRoots.ContainsKey(statement.Column))
        {
            throw new MiniDbException($"Column '{statement.Column}' already has an index.");
        }

        db.InWriteTransaction(() =>
        {
            db.Catalog.Replace(RowStore.AddIndex(db.Pool, table, column));
            return true;
        });
        return QueryResult.Affected(0);
    }

    /// <summary>Converts a literal to the column's type, or throws when they do not match.</summary>
    internal static object Coerce(ColumnDefinition column, object literal) => (column.Type, literal) switch
    {
        (ColumnType.Int32, long value) when value is >= int.MinValue and <= int.MaxValue => (int)value,
        (ColumnType.Int64, long value) => value,
        (ColumnType.Double, double value) => value,
        (ColumnType.Double, long value) => (double)value,
        (ColumnType.Bool, bool value) => value,
        (ColumnType.String, string value) => value,
        _ => throw new MiniDbException($"Column '{column.Name}' expects {column.Type}, but got {Describe(literal)}."),
    };

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
