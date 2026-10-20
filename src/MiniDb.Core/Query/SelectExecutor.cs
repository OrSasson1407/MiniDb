using MiniDb.Api;
using MiniDb.Catalog;
using MiniDb.Index;
using MiniDb.Records;

namespace MiniDb.Query;

/// <summary>
/// Runs a SELECT. A single table without grouping takes the fast path: rows are filtered as views and only the
/// projected values are boxed. Joins and aggregates work on tuples, one row per table. The access path is chosen
/// by <see cref="AccessPaths"/>, and ORDER BY is skipped when the path already returns rows in that order.
/// </summary>
internal static class SelectExecutor
{
    public static QueryResult Run(MiniDatabase db, SelectStatement statement)
    {
        var tables = new List<TableInfo> { Load(db, statement.Table) };
        tables.AddRange(statement.Joins.Select(join => Load(db, join.Table)));
        var scope = new ColumnScope(tables);
        bool aggregate = statement.GroupBy.Count > 0 || (statement.Items?.Any(item => item is AggregateItem) ?? false);
        int limit = statement.Limit ?? int.MaxValue;

        if (statement.Joins.Count == 0 && !aggregate)
        {
            return SingleTable(db, scope, statement, limit);
        }

        return Tuples(db, scope, statement, aggregate, limit);
    }

    private static QueryResult SingleTable(MiniDatabase db, ColumnScope scope, SelectStatement statement, int limit)
    {
        TableInfo table = scope.Table(0);
        AccessPlan plan = AccessPaths.ChoosePlan(db, table, statement.Where);
        List<ColumnRef> projection = Projection(scope, statement.Items);
        IReadOnlyList<string> header = HeaderOf(scope, statement.Items, projection);

        if (CanAnswerFromIndex(table, statement, plan, projection))
        {
            var range = (IndexRangePlan)plan;
            List<object?[]> covered = AccessPaths.IndexEntries(db, table, range)
                .Take(limit)
                .Select(entry => projection.Select(column => IndexedValue(table, column, entry)).ToArray())
                .ToList();
            return new QueryResult(header, covered, 0);
        }

        Func<RowView, bool>? filter = statement.Where is null ? null : Predicates.ForSingleTable(scope, statement.Where);
        IEnumerable<RowView> rows = AccessPaths.Rows(db, table, plan);
        if (filter is not null)
        {
            rows = rows.Where(filter);
        }

        if (statement.OrderBy.Count > 0 && !PlanReturnsOrder(table, plan, scope, statement.OrderBy))
        {
            List<object?[]> sorted = Ordering
                .FirstInOrder(rows.Select(row => new[] { row }), SortKeys(scope, statement.OrderBy), limit)
                .Select(tuple => Project(tuple, projection))
                .ToList();
            return new QueryResult(header, sorted, 0);
        }

        List<object?[]> output = rows.Take(limit).Select(row => Project(new[] { row }, projection)).ToList();
        return new QueryResult(header, output, 0);
    }

    private static QueryResult Tuples(MiniDatabase db, ColumnScope scope, SelectStatement statement, bool aggregate, int limit)
    {
        AccessPlan plan = statement.Joins.Count == 0 ? AccessPaths.ChoosePlan(db, scope.Table(0), statement.Where) : new FullScanPlan();
        IEnumerable<RowView> driving = AccessPaths.Rows(db, scope.Table(0), plan);
        IEnumerable<RowView[]> tuples = Joins.Run(db, scope, statement.Joins, driving);
        if (statement.Where is not null)
        {
            Func<RowView[], bool> filter = Predicates.ForTuple(scope, statement.Where);
            tuples = tuples.Where(filter);
        }

        if (aggregate)
        {
            return Grouped(scope, statement, tuples, limit);
        }

        List<ColumnRef> projection = Projection(scope, statement.Items);
        IReadOnlyList<string> header = HeaderOf(scope, statement.Items, projection);
        IEnumerable<RowView[]> ordered = statement.OrderBy.Count == 0
            ? tuples.Take(limit)
            : Ordering.FirstInOrder(tuples, SortKeys(scope, statement.OrderBy), limit);
        List<object?[]> output = ordered.Select(tuple => Project(tuple, projection)).ToList();
        return new QueryResult(header, output, 0);
    }

    private static QueryResult Grouped(ColumnScope scope, SelectStatement statement, IEnumerable<RowView[]> tuples, int limit)
    {
        if (statement.Items is null)
        {
            throw new MiniDbException("SELECT * cannot be combined with GROUP BY or an aggregate; list the columns.");
        }

        List<ColumnRef> groupBy = statement.GroupBy.Select(scope.Resolve).ToList();
        List<object?[]> rows = Aggregation.Run(tuples, scope, statement.Items, groupBy);
        IReadOnlyList<string> header = statement.Items.Select(WrittenName).ToList();

        if (statement.OrderBy.Count > 0)
        {
            List<(int Position, bool Descending)> keys = statement.OrderBy.Select(term =>
            {
                int position = header.ToList().IndexOf(term.Column);
                if (position < 0)
                {
                    throw new MiniDbException($"ORDER BY '{term.Column}' must name one of the selected columns.");
                }

                return (position, term.Descending);
            }).ToList();

            rows.Sort((left, right) =>
            {
                foreach ((int position, bool descending) in keys)
                {
                    int order = CompareNullable(left[position], right[position]);
                    if (order != 0)
                    {
                        return descending ? -order : order;
                    }
                }

                return 0;
            });
        }

        return new QueryResult(header, rows.Take(limit).ToList(), 0);
    }

    /// <summary>Nulls sort first. Other values are compared in their own type.</summary>
    private static int CompareNullable(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null ? (right is null ? 0 : -1) : 1;
        }

        return Comparison.CompareValues(left, right);
    }

    private static bool CanAnswerFromIndex(TableInfo table, SelectStatement statement, AccessPlan plan, List<ColumnRef> projection)
    {
        if (plan is not IndexRangePlan range || statement.Where is not ComparisonExpr || statement.OrderBy.Count > 0)
        {
            return false;
        }

        ColumnType indexed = table.Schema.Columns[ColumnScope.IndexOf(table.Schema, range.Column)].Type;
        if (indexed is not (ColumnType.Int32 or ColumnType.Int64))
        {
            return false;
        }

        return projection.All(column => column.Column == table.Schema.PrimaryKeyIndex || column.Name == range.Column);
    }

    /// <summary>True when rows come out of the plan already sorted by the single ascending ORDER BY column.</summary>
    private static bool PlanReturnsOrder(TableInfo table, AccessPlan plan, ColumnScope scope, IReadOnlyList<OrderTerm> orderBy)
    {
        if (orderBy.Count != 1 || orderBy[0].Descending)
        {
            return false;
        }

        ColumnRef column = scope.Resolve(orderBy[0].Column);
        return plan switch
        {
            PointLookupPlan or KeyRangePlan or FullScanPlan => column.Column == table.Schema.PrimaryKeyIndex,
            // A string index is ordered by prefix only, so it cannot answer an ORDER BY on the full string.
            IndexRangePlan range => column.Name == range.Column && column.Type is ColumnType.Int32 or ColumnType.Int64,
            _ => false,
        };
    }

    private static object? IndexedValue(TableInfo table, ColumnRef column, TreeKey entry)
    {
        if (column.Column == table.Schema.PrimaryKeyIndex)
        {
            return entry.Minor;
        }

        return column.Type == ColumnType.Int32 ? (int)entry.Major : entry.Major;
    }

    private static List<SortKey> SortKeys(ColumnScope scope, IReadOnlyList<OrderTerm> terms) =>
        terms.Select(term => new SortKey(scope.Resolve(term.Column), term.Descending)).ToList();

    /// <summary>The columns a SELECT returns. <c>*</c> means every column of every table.</summary>
    private static List<ColumnRef> Projection(ColumnScope scope, IReadOnlyList<SelectItem>? items)
    {
        if (items is not null)
        {
            return items.Select(item => scope.Resolve(((ColumnItem)item).Column)).ToList();
        }

        var all = new List<ColumnRef>();
        for (int table = 0; table < scope.TableCount; table++)
        {
            TableSchema schema = scope.Table(table).Schema;
            for (int column = 0; column < schema.Columns.Count; column++)
            {
                all.Add(new ColumnRef(table, column, schema.Columns[column].Type, schema.Columns[column].Name));
            }
        }

        return all;
    }

    /// <summary>Column names as the caller wrote them. For <c>*</c> over a join, names are qualified with their table.</summary>
    private static IReadOnlyList<string> HeaderOf(ColumnScope scope, IReadOnlyList<SelectItem>? items, List<ColumnRef> projection)
    {
        if (items is not null)
        {
            return items.Select(WrittenName).ToList();
        }

        bool qualify = scope.TableCount > 1;
        return projection
            .Select(column => qualify ? scope.Table(column.Table).Schema.Name + "." + column.Name : column.Name)
            .ToList();
    }

    private static string WrittenName(SelectItem item) => item switch
    {
        ColumnItem column => column.Column,
        AggregateItem aggregate => $"{aggregate.Kind.ToString().ToUpperInvariant()}({aggregate.Column ?? "*"})",
        _ => throw new InvalidOperationException("Unknown select item."),
    };

    private static object?[] Project(RowView[] tuple, List<ColumnRef> projection)
    {
        var values = new object?[projection.Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = tuple[projection[i].Table][projection[i].Column];
        }

        return values;
    }

    private static TableInfo Load(MiniDatabase db, string name) =>
        db.Catalog.Find(name) ?? throw new TableNotFoundException(name);
}
