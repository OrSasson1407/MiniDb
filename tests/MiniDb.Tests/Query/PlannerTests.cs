using MiniDb.Query;
using MiniDb.Records;

namespace MiniDb.Tests.Query;

public class PlannerTests
{
    private static readonly TableSchema Users = new(
        "users",
        new[]
        {
            new ColumnDefinition("id", ColumnType.Int64),
            new ColumnDefinition("name", ColumnType.String, maxLength: 50),
        },
        primaryKeyIndex: 0);

    [Fact]
    public void EqualityOnPrimaryKey_UsesPointLookup()
    {
        AccessPlan plan = Planner.Plan(Users, new Condition("id", CompareOp.Equal, 5L));

        Assert.Equal(new PointLookupPlan(5), plan);
    }

    [Fact]
    public void RangeOnPrimaryKey_UsesKeyRange()
    {
        AccessPlan plan = Planner.Plan(Users, new Condition("id", CompareOp.GreaterOrEqual, 5L));

        Assert.Equal(new KeyRangePlan(5, long.MaxValue), plan);
    }

    [Fact]
    public void ConditionOnOtherColumn_FallsBackToFullScan()
    {
        AccessPlan plan = Planner.Plan(Users, new Condition("name", CompareOp.Equal, "x"));

        Assert.IsType<FullScanPlan>(plan);
    }

    [Fact]
    public void NoWhere_FullScan()
    {
        Assert.IsType<FullScanPlan>(Planner.Plan(Users, null));
    }
}
