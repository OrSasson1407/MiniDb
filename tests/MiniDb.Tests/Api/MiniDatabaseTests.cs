using MiniDb.Api;
using MiniDb.Catalog;
using MiniDb.Database;
using MiniDb.Index;
using MiniDb.Records;
using MiniDb.Storage;

namespace MiniDb.Tests.Api;

public class MiniDatabaseTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("minidb-").FullName;

    private string DatabasePath => Path.Combine(_directory, "test.mdb");

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private static TableSchema UsersSchema() => new(
        "users",
        new[]
        {
            new ColumnDefinition("id", ColumnType.Int64),
            new ColumnDefinition("name", ColumnType.String, maxLength: 100),
        },
        primaryKeyIndex: 0);

    private static object?[] User(long id, string name) => new object?[] { id, name };

    [Fact]
    public void InsertThenReopen_GetReturnsTheRow()
    {
        using (var db = MiniDatabase.Open(DatabasePath))
        {
            db.CreateTable(UsersSchema());
            db.GetTable("users").Insert(User(1, "שלום"));
        }

        using var reopened = MiniDatabase.Open(DatabasePath);

        Assert.Equal(User(1, "שלום"), reopened.GetTable("users").Get(1));
    }

    [Fact]
    public void InsertDuplicateKey_Throws_AndOriginalRowIsKept()
    {
        using var db = MiniDatabase.Open(DatabasePath);
        db.CreateTable(UsersSchema());
        Table users = db.GetTable("users");
        users.Insert(User(1, "first"));

        Assert.Throws<DuplicateKeyException>(() => users.Insert(User(1, "second")));

        Assert.Equal(User(1, "first"), users.Get(1));
        Assert.Single(users.Scan());
    }

    [Fact]
    public void Delete_RemovesRow_AndMissingKeyReturnsFalse()
    {
        using var db = MiniDatabase.Open(DatabasePath);
        db.CreateTable(UsersSchema());
        Table users = db.GetTable("users");
        users.Insert(User(1, "a"));

        Assert.True(users.Delete(1));

        Assert.Null(users.Get(1));
        Assert.False(users.Delete(1));
        Assert.Empty(users.Scan());
    }

    [Fact]
    public void Scan_WithPredicate_ReturnsMatchingRows()
    {
        using var db = MiniDatabase.Open(DatabasePath);
        db.CreateTable(UsersSchema());
        Table users = db.GetTable("users");
        for (long id = 1; id <= 5; id++)
        {
            users.Insert(User(id, $"user{id}"));
        }

        var even = users.Scan(row => (long)row[0]! % 2 == 0).Select(row => (long)row[0]!).ToList();

        Assert.Equal(new long[] { 2, 4 }, even);
    }

    [Fact]
    public void RolledBackTransaction_InsertIsNotVisible()
    {
        using var db = MiniDatabase.Open(DatabasePath);
        db.CreateTable(UsersSchema());
        Table users = db.GetTable("users");

        using (var tx = db.BeginTransaction())
        {
            users.Insert(User(1, "temp"));
            tx.Rollback();
        }

        Assert.Null(users.Get(1));
    }

    [Fact]
    public void ManyRows_SpanHeapPagesAndTreeSplits_AndAllRemainReadable()
    {
        using var db = MiniDatabase.Open(DatabasePath);
        db.CreateTable(UsersSchema());
        Table users = db.GetTable("users");
        string padding = new('x', 60);
        for (long id = 0; id < 1000; id++)
        {
            users.Insert(User(id, $"{padding}{id}"));
        }

        Assert.Equal(1000, users.Scan().Count());
        Assert.Equal(User(777, $"{padding}777"), users.Get(777));
    }

    [Fact]
    public void CreateExistingTable_Throws()
    {
        using var db = MiniDatabase.Open(DatabasePath);
        db.CreateTable(UsersSchema());

        Assert.Throws<TableAlreadyExistsException>(() => db.CreateTable(UsersSchema()));
    }

    [Fact]
    public void GetMissingTable_Throws()
    {
        using var db = MiniDatabase.Open(DatabasePath);

        Assert.Throws<TableNotFoundException>(() => db.GetTable("missing"));
    }

    [Fact]
    public void OpeningAFileThatIsNotADatabase_Throws()
    {
        File.WriteAllBytes(DatabasePath, new byte[PageConstants.PageSize * 2]);

        Assert.Throws<InvalidDatabaseException>(() => MiniDatabase.Open(DatabasePath));
    }

    [Fact]
    public void FailedStatementInsideTransaction_AbortsTheWholeTransaction()
    {
        using var db = MiniDatabase.Open(DatabasePath);
        db.CreateTable(UsersSchema());
        Table users = db.GetTable("users");
        using var tx = db.BeginTransaction();
        users.Insert(User(1, "a"));

        Assert.Throws<DuplicateKeyException>(() => users.Insert(User(1, "b")));

        Assert.True(tx.IsFinished);
        Assert.Null(users.Get(1));
    }

    [Fact]
    public void SchemaErrorsFromSql_ComeOutAsMiniDbException()
    {
        using var db = MiniDatabase.Open(DatabasePath);

        Assert.Throws<MiniDbException>(() => db.Execute("CREATE TABLE t (id BIGINT PRIMARY KEY, s VARCHAR(0))"));
        Assert.Throws<MiniDbException>(() => db.Execute("CREATE TABLE t (id INT PRIMARY KEY)"));
        Assert.Throws<MiniDbException>(() => db.Execute("CREATE TABLE t (id BIGINT PRIMARY KEY, id INT)"));
    }

    [Fact]
    public void ForeignFile_IsRejected_WithoutLeavingALogBehind()
    {
        File.WriteAllBytes(DatabasePath, new byte[PageConstants.PageSize * 2]);

        Assert.Throws<InvalidDatabaseException>(() => MiniDatabase.Open(DatabasePath));
        Assert.False(File.Exists(DatabasePath + ".wal"));
    }

    [Fact]
    public void EmptyExistingFile_IsInitializedAsANewDatabase()
    {
        File.WriteAllBytes(DatabasePath, Array.Empty<byte>());

        using var db = MiniDatabase.Open(DatabasePath);
        db.CreateTable(UsersSchema());

        Assert.NotNull(db.GetTable("users"));
    }
}
