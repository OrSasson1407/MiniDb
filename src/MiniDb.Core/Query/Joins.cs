using MiniDb.Api;
using MiniDb.Catalog;
using MiniDb.Index;
using MiniDb.Records;

namespace MiniDb.Query;

/// <summary>
/// INNER JOIN execution. Each joined table is reached the cheapest way its join column allows: a primary-key lookup
/// for each row on the left, a probe of a secondary index, or otherwise a hash table built once from the joined table.
/// Joins run in FROM order, and the left-hand table is always read in full.
/// </summary>
internal static class Joins
{
    /// <summary>Builds the joined tuples: one row per table in FROM order, for every matching combination.</summary>
    public static IEnumerable<RowView[]> Run(
        MiniDatabase db,
        ColumnScope scope,
        IReadOnlyList<JoinClause> joins,
        IEnumerable<RowView> driving)
    {
        IEnumerable<RowView[]> tuples = driving.Select(row => new[] { row });
        for (int i = 0; i < joins.Count; i++)
        {
            tuples = JoinOne(db, scope, i + 1, joins[i], tuples);
        }

        return tuples;
    }

    private static IEnumerable<RowView[]> JoinOne(
        MiniDatabase db,
        ColumnScope scope,
        int position,
        JoinClause clause,
        IEnumerable<RowView[]> left)
    {
        ColumnRef a = scope.Resolve(clause.LeftColumn);
        ColumnRef b = scope.Resolve(clause.RightColumn);
        if (a.Table == position && b.Table < position)
        {
            (a, b) = (b, a);
        }

        if (a.Table >= position || b.Table != position)
        {
            throw new MiniDbException("A JOIN ON must compare a column of an earlier table with a column of the joined table.");
        }

        if (!Compatible(a.Type, b.Type))
        {
            throw new MiniDbException($"Cannot join '{a.Name}' ({a.Type}) with '{b.Name}' ({b.Type}).");
        }

        TableInfo table = scope.Table(position);
        if (IsInteger(b.Type) && b.Column == table.Schema.PrimaryKeyIndex)
        {
            return ProbePrimaryKey(db, table, a, left);
        }

        if (IsInteger(b.Type) && table.SecondaryIndexRoots.ContainsKey(b.Name))
        {
            return ProbeIndex(db, table, a, b.Name, left);
        }

        return HashJoin(db, table, a, b, left);
    }

    private static IEnumerable<RowView[]> ProbePrimaryKey(MiniDatabase db, TableInfo table, ColumnRef key, IEnumerable<RowView[]> left)
    {
        foreach (RowView[] tuple in left)
        {
            RowView? match = RowStore.GetView(db.Pool, table, ToLong(tuple[key.Table], key));
            if (match is { } row)
            {
                yield return Extend(tuple, row);
            }
        }
    }

    private static IEnumerable<RowView[]> ProbeIndex(MiniDatabase db, TableInfo table, ColumnRef key, string indexedColumn, IEnumerable<RowView[]> left)
    {
        var tree = BPlusTree.Open(db.Pool, table.SecondaryIndexRoots[indexedColumn]);
        foreach (RowView[] tuple in left)
        {
            long value = ToLong(tuple[key.Table], key);
            foreach ((TreeKey entry, _) in tree.Range(new TreeKey(value, long.MinValue), new TreeKey(value, long.MaxValue)))
            {
                RowView? match = RowStore.GetView(db.Pool, table, entry.Minor);
                if (match is { } row)
                {
                    yield return Extend(tuple, row);
                }
            }
        }
    }

    private static IEnumerable<RowView[]> HashJoin(
        MiniDatabase db,
        TableInfo table,
        ColumnRef key,
        ColumnRef joined,
        IEnumerable<RowView[]> left)
    {
        var build = new Dictionary<object, List<RowView>>();
        foreach (byte[] record in RowStore.ScanRecords(db.Pool, table))
        {
            var row = new RowView(table.Schema, record);
            object value = Normalize(row, joined);
            if (!build.TryGetValue(value, out List<RowView>? bucket))
            {
                bucket = new List<RowView>();
                build.Add(value, bucket);
            }

            bucket.Add(row);
        }

        foreach (RowView[] tuple in left)
        {
            if (!build.TryGetValue(Normalize(tuple[key.Table], key), out List<RowView>? bucket))
            {
                continue;
            }

            foreach (RowView row in bucket)
            {
                yield return Extend(tuple, row);
            }
        }
    }

    private static RowView[] Extend(RowView[] tuple, RowView row)
    {
        var extended = new RowView[tuple.Length + 1];
        tuple.CopyTo(extended, 0);
        extended[^1] = row;
        return extended;
    }

    private static long ToLong(RowView row, ColumnRef column) =>
        column.Type == ColumnType.Int32 ? row.GetInt32(column.Column) : row.GetInt64(column.Column);

    /// <summary>A hashable value: every integer type becomes a long so that INT and BIGINT columns match.</summary>
    private static object Normalize(RowView row, ColumnRef column) => column.Type switch
    {
        ColumnType.Int32 => (long)row.GetInt32(column.Column),
        ColumnType.Int64 => row.GetInt64(column.Column),
        ColumnType.Double => row.GetDouble(column.Column),
        ColumnType.Bool => row.GetBool(column.Column),
        ColumnType.String => row.GetString(column.Column),
        _ => throw new InvalidOperationException("Unknown column type."),
    };

    private static bool IsInteger(ColumnType type) => type is ColumnType.Int32 or ColumnType.Int64;

    private static bool Compatible(ColumnType a, ColumnType b) => (IsInteger(a) && IsInteger(b)) || a == b;
}
