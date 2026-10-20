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
        AccessPlan plan = Planner.Plan(Users, Cmp("id", CompareOp.Equal, 5L));

        Assert.Equal(new PointLookupPlan(5), plan);
    }

    [Fact]
    public void RangeOnPrimaryKey_UsesKeyRange()
    {
        AccessPlan plan = Planner.Plan(Users, Cmp("id", CompareOp.GreaterOrEqual, 5L));

        Assert.Equal(new KeyRangePlan(5, long.MaxValue), plan);
    }

    [Fact]
    public void ConditionOnOtherColumn_FallsBackToFullScan()
    {
        AccessPlan plan = Planner.Plan(Users, Cmp("name", CompareOp.Equal, "x"));

        Assert.IsType<FullScanPlan>(plan);
    }

    [Fact]
    public void NoWhere_FullScan()
    {
        Assert.IsType<FullScanPlan>(Planner.Plan(Users, null));
    }

    [Fact]
    public void EqualityOnIndexedIntColumn_UsesIndexRange()
    {
        var schema = new TableSchema(
            "scores",
            new[]
            {
                new ColumnDefinition("id", ColumnType.Int64),
                new ColumnDefinition("n", ColumnType.Int32),
            },
            primaryKeyIndex: 0);

        AccessPlan plan = Planner.Plan(schema, Cmp("n", CompareOp.Equal, 7L), new[] { "n" });

        Assert.Equal(new IndexRangePlan("n", 7, 7), plan);
    }

    [Fact]
    public void AndOfConditions_UsesTheCheapestRequiredPath()
    {
        Expr where = new AndExpr(Cmp("name", CompareOp.Equal, "x"), Cmp("id", CompareOp.Equal, 5L));

        Assert.Equal(new PointLookupPlan(5), Planner.Plan(Users, where));
    }

    [Fact]
    public void OrOfConditions_CannotNarrowTheRead_SoItScans()
    {
        Expr where = new OrExpr(Cmp("id", CompareOp.Equal, 5L), Cmp("id", CompareOp.Equal, 9L));

        Assert.IsType<FullScanPlan>(Planner.Plan(Users, where));
    }

    [Fact]
    public void QualifiedColumnOfThisTable_PlansLikeThePlainName()
    {
        Assert.Equal(new PointLookupPlan(5), Planner.Plan(Users, Cmp("users.id", CompareOp.Equal, 5L)));
    }

    private static ComparisonExpr Cmp(string column, CompareOp op, object value) => new(new Condition(column, op, value));
}
