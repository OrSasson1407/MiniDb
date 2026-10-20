using MiniDb.Database;
using MiniDb.Index;
using MiniDb.Storage;

namespace MiniDb.Tests.Index;

public class BPlusTreeTests
{
    /// <summary>A store whose page 0 is the database header, as in a real file.</summary>
    internal static MemoryPageStore NewStore()
    {
        var store = new MemoryPageStore();
        DatabaseFile.Create(store);
        return store;
    }

    [Fact]
    public void InsertThenFind_ReturnsLocation_AndMissingKeyReturnsNull()
    {
        var pool = new BufferPool(NewStore());
        var tree = BPlusTree.Create(pool);
        long[] keys = { 50, 10, 40, 20, 30 };
        foreach (long key in keys)
        {
            tree.Insert(key, new RowLocation(key + 1000, (int)key));
        }

        Assert.Equal(new RowLocation(1030, 30), tree.Find(30));
        Assert.Null(tree.Find(31));
    }

    [Fact]
    public void ThousandAscendingKeys_SplitLeaves_AndRangeReturnsAllInOrder()
    {
        var pool = new BufferPool(NewStore(), capacity: 32);
        var tree = BPlusTree.Create(pool);
        for (long key = 0; key < 1000; key++)
        {
            tree.Insert(key, new RowLocation(key, 0));
        }

        for (long key = 0; key < 1000; key++)
        {
            Assert.Equal(key, tree.Find(key)!.Value.PageId);
        }

        var keys = tree.Range(0, 999).Select(entry => entry.Key.Major).ToList();
        Assert.Equal(Enumerable.Range(0, 1000).Select(i => (long)i), keys);
    }

    [Fact]
    public void InsertDuplicateKey_Throws_AndLeavesTreeUnchanged()
    {
        var pool = new BufferPool(NewStore());
        var tree = BPlusTree.Create(pool);
        tree.Insert(5, new RowLocation(1, 1));

        Assert.Throws<DuplicateKeyException>(() => tree.Insert(5, new RowLocation(2, 2)));

        Assert.Equal(new RowLocation(1, 1), tree.Find(5));
    }

    [Fact]
    public void Delete_RemovesKey_AndMissingKeyReturnsFalse()
    {
        var pool = new BufferPool(NewStore());
        var tree = BPlusTree.Create(pool);
        tree.Insert(1, new RowLocation(1, 0));
        tree.Insert(2, new RowLocation(2, 0));

        Assert.True(tree.Delete(1));
        Assert.False(tree.Delete(1));
        Assert.Null(tree.Find(1));
        Assert.NotNull(tree.Find(2));
    }

    [Fact]
    public void Range_WithFromGreaterThanTo_IsEmpty()
    {
        var pool = new BufferPool(NewStore());
        var tree = BPlusTree.Create(pool);
        tree.Insert(1, new RowLocation(1, 0));

        Assert.Empty(tree.Range(5, 2));
    }

    [Fact]
    public void LargeSequentialLoad_SplitsInternalNodes_AndStaysSearchable()
    {
        var pool = new BufferPool(NewStore(), capacity: 64);
        var tree = BPlusTree.Create(pool);
        const int count = 30_000;
        for (long key = 0; key < count; key++)
        {
            tree.Insert(key, new RowLocation(key, 0));
            if (key % 1000 == 999)
            {
                pool.FlushAll();
            }
        }

        Assert.Equal(count, tree.Range(0, count - 1).Count());
        Assert.Equal(12_345, tree.Find(12_345)!.Value.PageId);
    }

    [Fact]
    public void AscendingLoad_FillsLeavesCompletely()
    {
        var store = NewStore();
        var pool = new BufferPool(store, capacity: 64);
        var tree = BPlusTree.Create(pool);
        for (long key = 0; key < 10_000; key++)
        {
            tree.Insert(key, new RowLocation(key, 0));
            if (key % 1000 == 999)
            {
                pool.FlushAll();
            }
        }

        pool.FlushAll();
        Assert.True(store.PageCount < 75, $"used {store.PageCount} pages");
    }

    [Fact]
    public void CompositeKeys_WithTheSameMajor_AreAllReturnedByRange()
    {
        var pool = new BufferPool(NewStore());
        var tree = BPlusTree.Create(pool);
        tree.Insert(new TreeKey(5, 1), new RowLocation(1, 0));
        tree.Insert(new TreeKey(5, 2), new RowLocation(2, 0));
        tree.Insert(new TreeKey(3, 9), new RowLocation(3, 0));

        var minors = tree.Range(new TreeKey(5, long.MinValue), new TreeKey(5, long.MaxValue))
            .Select(entry => entry.Key.Minor)
            .ToList();

        Assert.Equal(new long[] { 1, 2 }, minors);
    }

    [Fact]
    public void CompositeKeys_DeleteRemovesOnlyThatPair()
    {
        var pool = new BufferPool(NewStore());
        var tree = BPlusTree.Create(pool);
        tree.Insert(new TreeKey(5, 1), new RowLocation(1, 0));
        tree.Insert(new TreeKey(5, 2), new RowLocation(2, 0));

        Assert.True(tree.Delete(new TreeKey(5, 1)));

        Assert.Null(tree.Find(new TreeKey(5, 1)));
        Assert.NotNull(tree.Find(new TreeKey(5, 2)));
    }
}
