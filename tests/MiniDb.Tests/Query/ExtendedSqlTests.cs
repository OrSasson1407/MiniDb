using MiniDb.Api;
using MiniDb.Index;
using MiniDb.Query;
using MiniDb.Records;
using MiniDb.Transactions;

namespace MiniDb.Tests.Query;

/// <summary>AND/OR, ORDER BY, GROUP BY with aggregates, UPDATE, JOIN and string indexes, end to end.</summary>
public class ExtendedSqlTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("minidb-sql-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void WhereWithOrAndParentheses_SelectsTheRightRows()
    {
        using MiniDatabase db = Open();
        SeedUsers(db);

        Assert.Equal(new long[] { 1, 3 }, Ids(db.Execute("SELECT id FROM users WHERE score = 10 OR score = 30")));
        Assert.Equal(new long[] { 5 }, Ids(db.Execute("SELECT id FROM users WHERE (score = 10 OR score = 50) AND id > 1")));
        Assert.Equal(new long[] { 2, 3 }, Ids(db.Execute("SELECT id FROM users WHERE score >= 20 AND score <= 30")));
    }

    [Fact]
    public void OrderBy_SortsDescendingAndKeepsOnlyTheLimit()
    {
        using MiniDatabase db = Open();
        SeedUsers(db);

        Assert.Equal(new long[] { 5, 4 }, Ids(db.Execute("SELECT id FROM users ORDER BY score DESC LIMIT 2")));
        Assert.Equal("e", db.Execute("SELECT name FROM users ORDER BY score DESC LIMIT 1").Rows[0][0]);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, Ids(db.Execute("SELECT id FROM users ORDER BY id")));
    }

    [Fact]
    public void OrderBy_OnSecondaryIndexColumn_UsesIndexOrder()
    {
        using MiniDatabase db = Open();
        SeedUsers(db);
        db.Execute("CREATE INDEX ON users (score)");

        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, Ids(db.Execute("SELECT id FROM users WHERE score >= 10 ORDER BY score")));
    }

    [Fact]
    public void GroupBy_ComputesEveryAggregate()
    {
        using MiniDatabase db = Open();
        db.Execute("CREATE TABLE sales (id BIGINT PRIMARY KEY, region VARCHAR(10), amount INT)");
        db.Execute("INSERT INTO sales VALUES (1, 'north', 10)");
        db.Execute("INSERT INTO sales VALUES (2, 'north', 30)");
        db.Execute("INSERT INTO sales VALUES (3, 'south', 5)");

        QueryResult result = db.Execute(
            "SELECT region, COUNT(*), SUM(amount), MIN(amount), MAX(amount), AVG(amount) FROM sales GROUP BY region ORDER BY region");

        Assert.Equal(new object?[] { "north", 2L, 40L, 10, 30, 20.0 }, result.Rows[0]);
        Assert.Equal(new object?[] { "south", 1L, 5L, 5, 5, 5.0 }, result.Rows[1]);
    }

    [Fact]
    public void Aggregate_OverEmptyTable_ReturnsOneRow()
    {
        using MiniDatabase db = Open();
        db.Execute("CREATE TABLE empty_t (id BIGINT PRIMARY KEY, amount INT)");

        QueryResult result = db.Execute("SELECT COUNT(*), SUM(amount) FROM empty_t");

        Assert.Equal(new object?[] { 0L, null }, Assert.Single(result.Rows));
    }

    [Fact]
    public void GroupBy_SelectingAColumnOutsideTheGroup_IsAnError()
    {
        using MiniDatabase db = Open();
        db.Execute("CREATE TABLE sales (id BIGINT PRIMARY KEY, region VARCHAR(10), amount INT)");

        Assert.ThrowsAny<MiniDbException>(() => db.Execute("SELECT region, amount FROM sales GROUP BY region"));
    }

    [Fact]
    public void Update_ChangesOnlyTheMatchingRows()
    {
        using MiniDatabase db = Open();
        SeedUsers(db);

        QueryResult changed = db.Execute("UPDATE users SET score = 99 WHERE id = 2");

        Assert.Equal(1, changed.RowsAffected);
        Assert.Equal(99, db.Execute("SELECT score FROM users WHERE id = 2").Rows[0][0]);
        Assert.Equal(40, db.Execute("SELECT score FROM users WHERE id = 4").Rows[0][0]);
    }

    [Fact]
    public void Update_ChangingAnIndexedColumn_MovesTheIndexEntry()
    {
        using MiniDatabase db = Open();
        SeedUsers(db);
        db.Execute("CREATE INDEX ON users (score)");

        db.Execute("UPDATE users SET score = 77 WHERE id = 1");

        Assert.Equal(new long[] { 1 }, Ids(db.Execute("SELECT id FROM users WHERE score = 77")));
        Assert.Empty(db.Execute("SELECT id FROM users WHERE score = 10").Rows);
    }

    [Fact]
    public void Update_ChangingThePrimaryKey_MovesTheRow()
    {
        using MiniDatabase db = Open();
        SeedUsers(db);

        db.Execute("UPDATE users SET id = 100 WHERE id = 5");

        Assert.Equal(new long[] { 100 }, Ids(db.Execute("SELECT id FROM users WHERE id >= 100")));
        Assert.Empty(db.Execute("SELECT id FROM users WHERE id = 5").Rows);
    }

    [Fact]
    public void Update_ToADuplicateKey_FailsAndChangesNothing()
    {
        using MiniDatabase db = Open();
        SeedUsers(db);

        Assert.ThrowsAny<MiniDbException>(() => db.Execute("UPDATE users SET id = 1 WHERE id = 2"));

        Assert.Equal("b", db.Execute("SELECT name FROM users WHERE id = 2").Rows[0][0]);
    }

    [Fact]
    public void Join_OnNonKeyColumn_WithoutIndex_UsesHashJoin()
    {
        using MiniDatabase db = Open();
        SeedUsersAndOrders(db);

        QueryResult result = db.Execute(
            "SELECT users.name, orders.total FROM users JOIN orders ON users.id = orders.user_id WHERE orders.total > 5 ORDER BY orders.total DESC");

        Assert.Equal(new object?[] { "a", 50 }, result.Rows[0]);
        Assert.Equal(new object?[] { "b", 20 }, result.Rows[1]);
        Assert.Equal(new object?[] { "a", 10 }, result.Rows[2]);
        Assert.Equal(3, result.Rows.Count);
    }

    [Fact]
    public void Join_OnIndexedColumn_ReturnsTheSameRows()
    {
        using MiniDatabase db = Open();
        SeedUsersAndOrders(db);
        db.Execute("CREATE INDEX ON orders (user_id)");

        QueryResult result = db.Execute(
            "SELECT users.name, orders.total FROM users JOIN orders ON users.id = orders.user_id WHERE orders.total > 5 ORDER BY orders.total DESC");

        Assert.Equal(new object?[] { "a", 50 }, result.Rows[0]);
        Assert.Equal(3, result.Rows.Count);
    }

    [Fact]
    public void Join_OnPrimaryKeyOfTheJoinedTable_UsesLookups()
    {
        using MiniDatabase db = Open();
        SeedUsersAndOrders(db);

        QueryResult result = db.Execute("SELECT orders.id, users.name FROM orders JOIN users ON orders.user_id = users.id ORDER BY orders.id");

        Assert.Equal(new object?[] { 1L, "a" }, result.Rows[0]);
        Assert.Equal(new object?[] { 3L, "b" }, result.Rows[2]);
    }

    [Fact]
    public void SelectStar_OverAJoin_QualifiesColumnNames()
    {
        using MiniDatabase db = Open();
        SeedUsersAndOrders(db);

        QueryResult result = db.Execute("SELECT * FROM users JOIN orders ON users.id = orders.user_id LIMIT 1");

        Assert.Equal(new[] { "users.id", "users.name", "orders.id", "orders.user_id", "orders.total" }, result.Columns);
    }

    [Fact]
    public void Join_OnColumnsOfTheSameTable_IsRejected()
    {
        using MiniDatabase db = Open();
        SeedUsersAndOrders(db);

        Assert.ThrowsAny<MiniDbException>(() => db.Execute("SELECT users.name FROM users JOIN orders ON users.id = users.name"));
    }

    [Fact]
    public void StringIndex_FindsExactValuesEvenWhenPrefixesCollide()
    {
        using MiniDatabase db = Open();
        db.Execute("CREATE TABLE items (id BIGINT PRIMARY KEY, code VARCHAR(20))");
        db.Execute("INSERT INTO items VALUES (1, 'alpha-1')");
        db.Execute("INSERT INTO items VALUES (2, 'alpha-2')");
        db.Execute("INSERT INTO items VALUES (3, 'alpine')");
        db.Execute("INSERT INTO items VALUES (4, 'beta')");
        db.Execute("INSERT INTO items VALUES (5, 'alp')");
        db.Execute("CREATE INDEX ON items (code)");

        Assert.Equal(new long[] { 2 }, Ids(db.Execute("SELECT id FROM items WHERE code = 'alpha-2'")));
        Assert.Equal(new long[] { 5 }, Ids(db.Execute("SELECT id FROM items WHERE code < 'alpha'")));
        Assert.Equal(new long[] { 4 }, Ids(db.Execute("SELECT id FROM items WHERE code >= 'beta'")));
        Assert.Empty(db.Execute("SELECT id FROM items WHERE code = 'alpha-3'").Rows);
    }

    [Fact]
    public void StringPredicate_PlansAsAPrefixRange()
    {
        var schema = new TableSchema(
            "items",
            new[]
            {
                new ColumnDefinition("id", ColumnType.Int64),
                new ColumnDefinition("code", ColumnType.String, maxLength: 20),
            },
            primaryKeyIndex: 0);

        AccessPlan plan = Planner.Plan(schema, new ComparisonExpr(new Condition("code", CompareOp.Equal, "alpha")), new[] { "code" });

        long prefix = StringKey.Prefix("alpha");
        Assert.Equal(new IndexRangePlan("code", prefix, prefix), plan);
    }

    [Fact]
    public void RolledBackAllocations_ReturnToTheFreeList()
    {
        string path = Path.Combine(_directory, "alloc.mdb");
        long afterRollback;
        using (MiniDatabase db = MiniDatabase.Open(path))
        {
            db.Execute("CREATE TABLE t (id BIGINT PRIMARY KEY, name VARCHAR(40))");
            using (Transaction transaction = db.BeginTransaction())
            {
                WideRows(db.GetTable("t"), 0, 400);
                transaction.Rollback();
            }

            afterRollback = new FileInfo(path).Length;
            InsertWide(db, 0, 400);
        }

        long afterReuse = new FileInfo(path).Length;
        Assert.True(afterReuse <= afterRollback, $"file grew from {afterRollback} to {afterReuse} bytes: rolled-back pages were not reused");
    }

    private MiniDatabase Open() => MiniDatabase.Open(Path.Combine(_directory, "sql.mdb"));

    private static void SeedUsers(MiniDatabase db)
    {
        db.Execute("CREATE TABLE users (id BIGINT PRIMARY KEY, name VARCHAR(40), score INT)");
        db.Execute("INSERT INTO users VALUES (1, 'a', 10)");
        db.Execute("INSERT INTO users VALUES (2, 'b', 20)");
        db.Execute("INSERT INTO users VALUES (3, 'c', 30)");
        db.Execute("INSERT INTO users VALUES (4, 'd', 40)");
        db.Execute("INSERT INTO users VALUES (5, 'e', 50)");
    }

    private static void SeedUsersAndOrders(MiniDatabase db)
    {
        db.Execute("CREATE TABLE users (id BIGINT PRIMARY KEY, name VARCHAR(40))");
        db.Execute("INSERT INTO users VALUES (1, 'a')");
        db.Execute("INSERT INTO users VALUES (2, 'b')");
        db.Execute("INSERT INTO users VALUES (3, 'c')");
        db.Execute("CREATE TABLE orders (id BIGINT PRIMARY KEY, user_id BIGINT, total INT)");
        db.Execute("INSERT INTO orders VALUES (1, 1, 10)");
        db.Execute("INSERT INTO orders VALUES (2, 3, 4)");
        db.Execute("INSERT INTO orders VALUES (3, 2, 20)");
        db.Execute("INSERT INTO orders VALUES (4, 1, 50)");
    }

    private static void InsertWide(MiniDatabase db, long from, long count)
    {
        using Transaction transaction = db.BeginTransaction();
        WideRows(db.GetTable("t"), from, count);
        transaction.Commit();
    }

    /// <summary>Rows wide enough that a few hundred of them need many pages. Runs inside the caller's transaction.</summary>
    private static void WideRows(Table table, long from, long count)
    {
        for (long id = from; id < from + count; id++)
        {
            table.Insert(new object?[] { id, new string('x', 30) });
        }
    }

    private static long[] Ids(QueryResult result) => result.Rows.Select(row => Convert.ToInt64(row[0])).ToArray();
}
