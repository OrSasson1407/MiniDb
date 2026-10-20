using MiniDb.Index;
using MiniDb.Records;

namespace MiniDb.Query;

/// <summary>How the executor reaches the rows a statement needs.</summary>
public abstract record AccessPlan;

/// <summary>One row by primary key.</summary>
public sealed record PointLookupPlan(long Key) : AccessPlan;

/// <summary>Rows whose primary key lies in [From, To], walked through the B+ tree.</summary>
public sealed record KeyRangePlan(long From, long To) : AccessPlan;

/// <summary>Every row of the table, read along the heap chain.</summary>
public sealed record FullScanPlan : AccessPlan;

/// <summary>Rows whose value in a secondary-indexed integer column lies in [From, To].</summary>
public sealed record IndexRangePlan(string Column, long From, long To) : AccessPlan;

public static class Planner
{
    /// <summary>
    /// Chooses an access path from the comparisons that a WHERE clause requires (the AND-ed ones). A condition on the
    /// primary key uses its tree; a condition on an indexed integer column uses that column's secondary index. The
    /// cheapest of those wins. An OR can narrow nothing on its own, so it is only checked by the filter.
    /// The whole WHERE clause is still applied to the rows the plan returns.
    /// </summary>
    public static AccessPlan Plan(TableSchema schema, Expr? where, IEnumerable<string>? indexedColumns = null)
    {
        var indexed = indexedColumns?.ToList() ?? new List<string>();
        var required = new List<Condition>();
        if (where is not null)
        {
            CollectRequired(where, required);
        }

        AccessPlan best = new FullScanPlan();
        foreach (Condition condition in required)
        {
            AccessPlan candidate = PlanCondition(schema, condition, indexed);
            if (Rank(candidate) < Rank(best))
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>Column names are compared without a leading table qualifier, so <c>users.id</c> plans like <c>id</c>.</summary>
    public static string Unqualify(string tableName, string column) =>
        column.StartsWith(tableName + ".", StringComparison.Ordinal) ? column[(tableName.Length + 1)..] : column;

    private static void CollectRequired(Expr expr, List<Condition> into)
    {
        switch (expr)
        {
            case AndExpr and:
                CollectRequired(and.Left, into);
                CollectRequired(and.Right, into);
                break;
            case ComparisonExpr comparison:
                into.Add(comparison.Condition);
                break;
        }
    }

    private static AccessPlan PlanCondition(TableSchema schema, Condition condition, List<string> indexed)
    {
        string column = Unqualify(schema.Name, condition.Column);
        if (condition.Value is string text)
        {
            return indexed.Contains(column) && IsColumnOfType(schema, column, ColumnType.String)
                ? StringIndexPlan(column, condition.Op, StringKey.Prefix(text))
                : new FullScanPlan();
        }

        if (condition.Value is not long key)
        {
            return new FullScanPlan();
        }

        if (column == schema.PrimaryKey.Name)
        {
            return PrimaryKeyPlan(condition.Op, key);
        }

        return indexed.Contains(column) && !IsColumnOfType(schema, column, ColumnType.String)
            ? IndexPlan(column, condition.Op, key)
            : new FullScanPlan();
    }

    private static bool IsColumnOfType(TableSchema schema, string column, ColumnType type) =>
        schema.Columns.Any(c => c.Name == column && c.Type == type);

    /// <summary>
    /// A range of string-index keys that contains every row whose string satisfies the condition. Strings that share the
    /// prefix share a key, so the executor re-checks each row's full value.
    /// </summary>
    private static AccessPlan StringIndexPlan(string column, CompareOp op, long prefix) => op switch
    {
        CompareOp.Equal => new IndexRangePlan(column, prefix, prefix),
        CompareOp.Less or CompareOp.LessOrEqual => new IndexRangePlan(column, long.MinValue, prefix),
        CompareOp.Greater or CompareOp.GreaterOrEqual => new IndexRangePlan(column, prefix, long.MaxValue),
        _ => new FullScanPlan(),
    };

    private static int Rank(AccessPlan plan) => plan switch
    {
        PointLookupPlan => 0,
        KeyRangePlan => 1,
        IndexRangePlan => 2,
        _ => 3,
    };

    private static AccessPlan PrimaryKeyPlan(CompareOp op, long key) => op switch
    {
        CompareOp.Equal => new PointLookupPlan(key),
        CompareOp.LessOrEqual => new KeyRangePlan(long.MinValue, key),
        CompareOp.GreaterOrEqual => new KeyRangePlan(key, long.MaxValue),
        CompareOp.Less when key > long.MinValue => new KeyRangePlan(long.MinValue, key - 1),
        CompareOp.Greater when key < long.MaxValue => new KeyRangePlan(key + 1, long.MaxValue),
        _ => new FullScanPlan(),
    };

    private static AccessPlan IndexPlan(string column, CompareOp op, long key) => op switch
    {
        CompareOp.Equal => new IndexRangePlan(column, key, key),
        CompareOp.LessOrEqual => new IndexRangePlan(column, long.MinValue, key),
        CompareOp.GreaterOrEqual => new IndexRangePlan(column, key, long.MaxValue),
        CompareOp.Less when key > long.MinValue => new IndexRangePlan(column, long.MinValue, key - 1),
        CompareOp.Greater when key < long.MaxValue => new IndexRangePlan(column, key + 1, long.MaxValue),
        _ => new FullScanPlan(),
    };
}
