using MiniDb.Api;
using MiniDb.Catalog;
using MiniDb.Records;

namespace MiniDb.Query;

/// <summary>
/// Runs an UPDATE inside the write transaction. Matching rows are read first, then each one is rewritten: when the
/// primary key stays the same the row is replaced in its leaf and only the changed secondary indexes are touched;
/// when it changes the row is deleted and inserted again.
/// </summary>
internal static class UpdateExecutor
{
    public static QueryResult Run(MiniDatabase db, UpdateStatement statement)
    {
        TableInfo original = db.Catalog.Find(statement.Table) ?? throw new TableNotFoundException(statement.Table);
        TableInfo table = original;
        TableSchema schema = table.Schema;
        var scope = new ColumnScope(new[] { table });
        List<(int Column, object? Value)> assignments = ResolveAssignments(schema, statement.Assignments);
        Func<RowView, bool> filter = Predicates.ForSingleTable(scope, statement.Where);

        List<RowView> matches = AccessPaths.Rows(db, table, AccessPaths.ChoosePlan(db, table, statement.Where))
            .Where(filter)
            .ToList();

        int key = schema.PrimaryKeyIndex;
        foreach (RowView row in matches)
        {
            object?[] values = Enumerable.Range(0, schema.Columns.Count).Select(i => row[i]).ToArray();
            foreach ((int column, object? value) in assignments)
            {
                values[column] = value;
            }

            long oldKey = row.GetInt64(key);
            long newKey = (long)values[key]!;
            if (newKey == oldKey)
            {
                table = RowStore.Update(db.Pool, table, oldKey, row, values);
            }
            else
            {
                RowStore.Delete(db.Pool, table, oldKey);
                table = RowStore.Insert(db.Pool, table, values);
            }
        }

        if (!ReferenceEquals(table, original))
        {
            db.Catalog.Replace(table);
        }

        return QueryResult.Affected(matches.Count);
    }

    private static List<(int Column, object? Value)> ResolveAssignments(TableSchema schema, IReadOnlyList<Assignment> assignments)
    {
        var resolved = new List<(int Column, object? Value)>();
        foreach (Assignment assignment in assignments)
        {
            int column = ColumnScope.IndexOf(schema, assignment.Column);
            if (column < 0)
            {
                throw new MiniDbException($"Unknown column '{assignment.Column}'.");
            }

            if (resolved.Any(existing => existing.Column == column))
            {
                throw new MiniDbException($"Column '{assignment.Column}' is set more than once.");
            }

            resolved.Add((column, SqlEngine.Coerce(schema.Columns[column], assignment.Value)));
        }

        return resolved;
    }
}
