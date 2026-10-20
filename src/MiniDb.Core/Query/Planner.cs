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
    /// Chooses an access path. A condition on the primary key uses its tree; a condition on an indexed integer
    /// column uses that column's secondary index; anything else scans the heap.
    /// </summary>
    public static AccessPlan Plan(TableSchema schema, Condition? where, IEnumerable<string>? indexedColumns = null)
    {
        if (where is null || where.Value is not long key)
        {
            return new FullScanPlan();
        }

        if (where.Column == schema.PrimaryKey.Name)
        {
            return PrimaryKeyPlan(where.Op, key);
        }

        if (indexedColumns is not null && indexedColumns.Contains(where.Column))
        {
            return IndexPlan(where.Column, where.Op, key);
        }

        return new FullScanPlan();
    }

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
