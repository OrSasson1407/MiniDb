using MiniDb.Database;
using MiniDb.Index;
using MiniDb.Records;
using MiniDb.Storage;

namespace MiniDb.Tests.Index;

public class ClusteredTreeTests
{
    private static MemoryPageStore NewStore()
    {
        var store = new MemoryPageStore();
        DatabaseFile.Create(store);
        return store;
    }

    private static byte[] Record(int size, byte fill) => Enumerable.Repeat(fill, size).ToArray();

    [Fact]
    public void InsertThenFind_ReturnsTheRecord_ForVariableSizes()
    {
        var tree = ClusteredTree.Create(new BufferPool(NewStore()));
        tree.Insert(5, Record(10, 1));
        tree.Insert(2, Record(3000, 2));

        Assert.Equal(Record(10, 1), tree.Find(5));
        Assert.Equal(Record(3000, 2), tree.Find(2));
        Assert.Null(tree.Find(9));
    }

    [Fact]
    public void Range_ReturnsRecordsInKeyOrder()
    {
        var tree = ClusteredTree.Create(new BufferPool(NewStore(), capacity: 64));
        for (long key = 999; key >= 0; key--)
        {
            tree.Insert(key, Record(40, (byte)key));
        }

        var keys = tree.Range(0, 999).Select(entry => entry.Key).ToList();

        Assert.Equal(Enumerable.Range(0, 1000).Select(i => (long)i), keys);
    }

    [Fact]
    public void Delete_RemovesOnlyThatRecord()
    {
        var tree = ClusteredTree.Create(new BufferPool(NewStore()));
        tree.Insert(1, Record(8, 1));
        tree.Insert(2, Record(8, 2));

        Assert.True(tree.Delete(1));
        Assert.False(tree.Delete(1));
        Assert.Null(tree.Find(1));
        Assert.Equal(Record(8, 2), tree.Find(2));
    }

    [Fact]
    public void InsertDuplicateKey_Throws()
    {
        var tree = ClusteredTree.Create(new BufferPool(NewStore()));
        tree.Insert(4, Record(8, 1));

        Assert.Throws<DuplicateKeyException>(() => tree.Insert(4, Record(8, 2)));
    }

    [Fact]
    public void RecordLargerThanAPage_IsRejected()
    {
        var tree = ClusteredTree.Create(new BufferPool(NewStore()));

        Assert.Throws<RecordTooLargeException>(() => tree.Insert(1, Record(4100, 1)));
    }

    [Fact]
    public void RandomOperations_MatchSortedDictionaryModel()
    {
        var random = new Random(11);
        var tree = ClusteredTree.Create(new BufferPool(NewStore(), capacity: 64));
        var model = new SortedDictionary<long, byte[]>();

        for (int step = 0; step < 4000; step++)
        {
            long key = random.NextInt64(-500, 500);
            if (random.Next(3) == 0)
            {
                Assert.Equal(model.Remove(key), tree.Delete(key));
            }
            else if (!model.ContainsKey(key))
            {
                byte[] record = Record(random.Next(1, 300), (byte)random.Next(256));
                tree.Insert(key, record);
                model[key] = record;
            }

            Assert.Equal(model.TryGetValue(key, out byte[]? expected) ? expected : null, tree.Find(key));
        }

        var all = tree.Range(long.MinValue, long.MaxValue)
            .Select(cell => (cell.Key, Convert.ToHexString(cell.Record)))
            .ToList();
        Assert.Equal(model.Select(pair => (pair.Key, Convert.ToHexString(pair.Value))), all);
    }
}
