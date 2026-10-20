using MiniDb.Api;
using MiniDb.Index;
using MiniDb.Query;
using MiniDb.Storage;

namespace MiniDb.Tests.Query;

public class SqlExecutionTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("minidb-sql-").FullName;

    private string DatabasePath => Path.Combine(_directory, "sql.mdb");

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private static void CreateUsers(MiniDatabase db)
    {
        db.Execute("CREATE TABLE users (id BIGINT PRIMARY KEY, name VARCHAR(50))");
    }

    [Fact]
    public void InsertThenSelectWhereKey_ReturnsTheRow()
    {
        using var db = MiniDatabase.Open(DatabasePath);
        CreateUsers(db);
        db.Execute("INSERT INTO users VALUES (1, 'שלום')");

        QueryResult result = db.Execute("SELECT * FROM users WHERE id = 1");

        Assert.Equal(new[] { "id", "name" }, result.Columns);
        Assert.Equal(new object?[] { 1L, "שלום" }, Assert.Single(result.Rows));
    }

    [Fact]
    public void RangeWithProjection_ReturnsOnlyRequestedColumnInKeyOrder()
    {
        using var db = MiniDatabase.Open(DatabasePath);
        CreateUsers(db);
        for (int id = 1; id <= 5; id++)
        {
            db.Execute($"INSERT INTO users VALUES ({id}, 'user{id}')");
        }

        QueryResult result = db.Execute("SELECT name FROM users WHERE id >= 4");

        Assert.Equal(new object?[] { "user4" }, result.Rows[0]);
        Assert.Equal(new object?[] { "user5" }, result.Rows[1]);
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void Limit_CapsTheNumberOfRows()
    {
        using var db = MiniDatabase.Open(DatabasePath);
        CreateUsers(db);
        for (int id = 1; id <= 5; id++)
        {
            db.Execute($"INSERT INTO users VALUES ({id}, 'u')");
        }

        Assert.Equal(2, db.Execute("SELECT * FROM users LIMIT 2").Rows.Count);
    }

    [Fact]
    public void DeleteWhereKeyMissing_AffectsNoRows_AndDoesNotThrow()
    {
        using var db = MiniDatabase.Open(DatabasePath);
        CreateUsers(db);

        QueryResult result = db.Execute("DELETE FROM users WHERE id = 999");

        Assert.Equal(0, result.RowsAffected);
    }

    [Fact]
    public void DeleteWithRange_RemovesMatchingRowsOnly()
    {
        using var db = MiniDatabase.Open(DatabasePath);
        CreateUsers(db);
        for (int id = 1; id <= 5; id++)
        {
            db.Execute($"INSERT INTO users VALUES ({id}, 'u')");
        }

        Assert.Equal(2, db.Execute("DELETE FROM users WHERE id < 3").RowsAffected);
        Assert.Equal(3, db.Execute("SELECT * FROM users").Rows.Count);
    }

    [Fact]
    public void InsertStringIntoBigintColumn_Throws()
    {
        using var db = MiniDatabase.Open(DatabasePath);
        CreateUsers(db);

        Assert.Throws<MiniDbException>(() => db.Execute("INSERT INTO users VALUES ('one', 'x')"));
    }

    [Fact]
    public void UnknownColumnInWhere_Throws()
    {
        using var db = MiniDatabase.Open(DatabasePath);
        CreateUsers(db);

        Assert.Throws<MiniDbException>(() => db.Execute("SELECT * FROM users WHERE missing = 1"));
    }

    [Fact]
    public void DuplicateKeyThroughSql_Throws()
    {
        using var db = MiniDatabase.Open(DatabasePath);
        CreateUsers(db);
        db.Execute("INSERT INTO users VALUES (1, 'a')");

        Assert.Throws<DuplicateKeyException>(() => db.Execute("INSERT INTO users VALUES (1, 'b')"));
    }
}
