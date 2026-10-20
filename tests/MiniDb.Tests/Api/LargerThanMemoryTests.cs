using MiniDb.Api;
using MiniDb.Storage;
using MiniDb.Transactions;

namespace MiniDb.Tests.Api;

/// <summary>The data set is several times larger than the buffer pool, so pages are evicted and read back constantly.</summary>
public class LargerThanMemoryTests : IDisposable
{
    private const int Rows = 30_000;
    private const int PoolPages = 128;

    private readonly string _directory = Directory.CreateTempSubdirectory("minidb-big-").FullName;
    private string Path_ => Path.Combine(_directory, "big.mdb");

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Rows_SurviveEviction_AndAreFoundByKeyIndexAndScan()
    {
        using (MiniDatabase db = Open())
        {
            Load(db);
            Verify(db);
        }

        using MiniDatabase reopened = Open();
        Verify(reopened);
    }

    [Fact]
    public void ThePool_IsMuchSmallerThanTheData()
    {
        using MiniDatabase db = Open();
        Load(db);

        long dataPages = new FileInfo(Path_).Length / PageConstants.PageSize;

        Assert.True(dataPages > PoolPages * 4, $"the file has {dataPages} pages, the pool {PoolPages}");
    }

    [Fact]
    public async Task ConcurrentReaders_OnASmallPool_GetCorrectRows()
    {
        using MiniDatabase db = Open();
        Load(db);
        Table users = db.GetTable("users");

        await Task.WhenAll(Enumerable.Range(0, 6).Select(t => Task.Run(() =>
        {
            var random = new Random(t);
            for (int i = 0; i < 4000; i++)
            {
                long id = random.NextInt64(Rows);
                object?[]? row = users.Get(id);
                Assert.Equal($"user-{id:D8}", row![1]);
            }
        }))).WaitAsync(TimeSpan.FromSeconds(60));
    }

    private MiniDatabase Open() => MiniDatabase.Open(Path_, new DatabaseOptions { BufferPoolPages = PoolPages });

    private static void Load(MiniDatabase db)
    {
        db.Execute("CREATE TABLE users (id BIGINT PRIMARY KEY, name VARCHAR(32), score INT)");
        Table users = db.GetTable("users");
        for (int start = 0; start < Rows; start += 5000)
        {
            using Transaction transaction = db.BeginTransaction();
            for (long id = start; id < start + 5000; id++)
            {
                users.Insert(new object?[] { id, $"user-{id:D8}", (int)(id % 1000) });
            }

            transaction.Commit();
        }

        db.Execute("CREATE INDEX ON users (score)");
    }

    private static void Verify(MiniDatabase db)
    {
        Table users = db.GetTable("users");
        for (long id = 0; id < Rows; id += 97)
        {
            Assert.Equal($"user-{id:D8}", users.Get(id)![1]);
        }

        Assert.Equal(Rows, users.Scan().Count());
        Assert.Equal(Rows / 1000, db.Execute("SELECT id FROM users WHERE score = 7").Rows.Count);
        Assert.Equal(Rows / 2, db.Execute("SELECT id FROM users WHERE score < 500").Rows.Count);
    }
}
