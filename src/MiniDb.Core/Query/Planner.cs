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

public static class Planner
{
    /// <summary>Uses the primary-key tree when the condition is on the primary key with an integer value; otherwise scans.</summary>
    public static AccessPlan Plan(TableSchema schema, Condition? where)
    {
        if (where is null || where.Column != schema.PrimaryKey.Name || where.Value is not long key)
        {
            return new FullScanPlan();
        }

        return where.Op switch
        {
            CompareOp.Equal => new PointLookupPlan(key),
            CompareOp.LessOrEqual => new KeyRangePlan(long.MinValue, key),
            CompareOp.GreaterOrEqual => new KeyRangePlan(key, long.MaxValue),
            CompareOp.Less when key > long.MinValue => new KeyRangePlan(long.MinValue, key - 1),
            CompareOp.Greater when key < long.MaxValue => new KeyRangePlan(key + 1, long.MaxValue),
            _ => new FullScanPlan(),
        };
    }
}
