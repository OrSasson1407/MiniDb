using MiniDb.Catalog;
using MiniDb.Database;
using MiniDb.Records;
using MiniDb.Storage;

namespace MiniDb.Tests.Catalog;

public class TableCatalogTests
{
    /// <summary>A store whose page 0 is the database header, as in a real file.</summary>
    private static MemoryPageStore NewStore()
    {
        var store = new MemoryPageStore();
        DatabaseFile.Create(store);
        return store;
    }

    private static TableInfo UsersTable(string name = "users") => new(
        new TableSchema(
            name,
            new[]
            {
                new ColumnDefinition("id", ColumnType.Int64),
                new ColumnDefinition("name", ColumnType.String, maxLength: 50),
            },
            primaryKeyIndex: 0),
        PrimaryKeyRoot: 7,
        SecondaryIndexRoots: new Dictionary<string, long> { ["name"] = 9 });

    [Fact]
    public void CreateThenFind_AfterReopen_ReturnsSameSchemaAndRoots()
    {
        var store = NewStore();
        var pool = new BufferPool(store);
        long root = TableCatalog.CreateRoot(pool);
        new TableCatalog(pool, root).Create(UsersTable());
        pool.FlushAll();

        TableInfo? found = new TableCatalog(new BufferPool(store), root).Find("users");

        Assert.NotNull(found);
        Assert.Equal("users", found.Schema.Name);
        Assert.Equal(2, found.Schema.Columns.Count);
        Assert.Equal("name", found.Schema.Columns[1].Name);
        Assert.Equal(50, found.Schema.Columns[1].MaxLength);
        Assert.Equal(0, found.Schema.PrimaryKeyIndex);
        Assert.Equal(7, found.PrimaryKeyRoot);
        Assert.Equal(9, found.SecondaryIndexRoots["name"]);
    }

    [Fact]
    public void CreateTableWithExistingName_Throws_AndLeavesCatalogUnchanged()
    {
        var pool = new BufferPool(NewStore());
        var catalog = new TableCatalog(pool, TableCatalog.CreateRoot(pool));
        catalog.Create(UsersTable());

        Assert.Throws<TableAlreadyExistsException>(() => catalog.Create(UsersTable()));

        Assert.Single(catalog.All());
    }

    [Fact]
    public void ManyTables_SpanMultiplePages_AndAllAreListed()
    {
        var store = NewStore();
        var pool = new BufferPool(store, capacity: 16);
        var catalog = new TableCatalog(pool, TableCatalog.CreateRoot(pool));
        string longName = new('t', 60);

        for (int i = 0; i < 200; i++)
        {
            catalog.Create(UsersTable($"{longName}{i}"));
        }

        Assert.True(store.PageCount > 2);
        Assert.Equal(200, catalog.All().Count());
    }

    [Fact]
    public void Find_UnknownTable_ReturnsNull()
    {
        var pool = new BufferPool(NewStore());
        var catalog = new TableCatalog(pool, TableCatalog.CreateRoot(pool));

        Assert.Null(catalog.Find("missing"));
    }

    [Fact]
    public void RepeatedReplace_DoesNotGrowTheStore()
    {
        var store = NewStore();
        var pool = new BufferPool(store);
        var catalog = new TableCatalog(pool, TableCatalog.CreateRoot(pool));
        catalog.Create(UsersTable());
        long pagesBefore = store.PageCount;

        for (int i = 0; i < 500; i++)
        {
            catalog.Replace(UsersTable());
        }

        Assert.Equal(pagesBefore, store.PageCount);
    }
}
