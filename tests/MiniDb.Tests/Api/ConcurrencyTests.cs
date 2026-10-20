using System.Collections.Concurrent;
using MiniDb.Api;
using MiniDb.Records;
using MiniDb.Storage;

namespace MiniDb.Tests.Api;

public class ConcurrencyTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("minidb-conc-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void BufferPool_ConcurrentPinsAndUnpins_ReturnTheRightPages()
    {
        var store = new MemoryPageStore();
        for (int id = 0; id < 64; id++)
        {
            long page = store.AllocatePage();
            var data = new byte[PageConstants.PageSize];
            data[0] = (byte)page;
            PageChecksum.Stamp(data);
            store.WritePage(page, data);
        }

        var pool = new BufferPool(store, capacity: 16);
        Parallel.For(0, 8, thread =>
        {
            var random = new Random(thread);
            for (int i = 0; i < 3000; i++)
            {
                long id = random.Next(64);
                using PinnedPage page = pool.Pin(id);
                if (page.Data[0] != (byte)id)
                {
                    throw new InvalidOperationException($"Pinned page {id} returned another page's bytes.");
                }
            }
        });
    }

    [Fact]
    public async Task ConcurrentReaders_AndOneWriter_SeeConsistentRows()
    {
        using var db = MiniDatabase.Open(Path.Combine(_directory, "concurrent.mdb"));
        db.CreateTable(new TableSchema(
            "users",
            new[]
            {
                new ColumnDefinition("id", ColumnType.Int64),
                new ColumnDefinition("name", ColumnType.String, maxLength: 20),
            },
            primaryKeyIndex: 0));
        Table users = db.GetTable("users");
        for (long id = 0; id < 1000; id++)
        {
            users.Insert(new object?[] { id, $"n{id}" });
        }

        var errors = new ConcurrentBag<Exception>();
        var readers = Enumerable.Range(0, 4).Select(thread => Task.Run(() =>
        {
            var random = new Random(thread);
            try
            {
                for (int i = 0; i < 2000; i++)
                {
                    long id = random.NextInt64(0, 1000);
                    object?[]? row = users.Get(id);
                    if (row is null || (string)row[1]! != $"n{id}")
                    {
                        throw new InvalidOperationException($"Row {id} is missing or wrong.");
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        })).ToArray();

        Task writer = Task.Run(() =>
        {
            try
            {
                for (long id = 1000; id < 1500; id++)
                {
                    users.Insert(new object?[] { id, $"n{id}" });
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        });

        await Task.WhenAll(readers.Append(writer));

        Assert.Empty(errors);
        Assert.Equal(1500, users.Scan().Count());
    }
}
