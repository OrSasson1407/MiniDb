using MiniDb.Storage;

namespace MiniDb.Tests.Storage;

public class PageStoreTests
{
    [Theory]
    [InlineData("memory")]
    [InlineData("file")]
    public void WrittenPage_IsReadBackIdentically(string kind)
    {
        WithStore(kind, store =>
        {
            long id = store.AllocatePage();
            var data = new byte[PageConstants.PageSize];
            data[0] = 0xAB;
            data[^1] = 0xCD;

            store.WritePage(id, data);
            var read = new byte[PageConstants.PageSize];
            store.ReadPage(id, read);

            Assert.Equal(data, read);
        });
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("file")]
    public void ReadingUnallocatedPage_Throws(string kind)
    {
        WithStore(kind, store =>
        {
            var buffer = new byte[PageConstants.PageSize];
            Assert.Throws<PageOutOfRangeException>(() => store.ReadPage(5, buffer));
        });
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("file")]
    public void AllocatePage_ReturnsConsecutiveIdsFromZero(string kind)
    {
        WithStore(kind, store =>
        {
            Assert.Equal(0, store.AllocatePage());
            Assert.Equal(1, store.AllocatePage());
            Assert.Equal(2, store.PageCount);
        });
    }

    [Fact]
    public void DataSurvivesReopen()
    {
        string path = TempPath();
        try
        {
            var data = new byte[PageConstants.PageSize];
            data[100] = 7;
            using (var store = new FilePageStore(path, create: true))
            {
                long id = store.AllocatePage();
                store.WritePage(id, data);
                store.Sync();
            }

            using var reopened = new FilePageStore(path, create: false);
            var read = new byte[PageConstants.PageSize];
            reopened.ReadPage(0, read);

            Assert.Equal(data, read);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void WithStore(string kind, Action<IPageStore> test)
    {
        if (kind == "memory")
        {
            using var memory = new MemoryPageStore();
            test(memory);
            return;
        }

        string path = TempPath();
        try
        {
            using var file = new FilePageStore(path, create: true);
            test(file);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"minidb-{Guid.NewGuid():N}.mdb");
}
