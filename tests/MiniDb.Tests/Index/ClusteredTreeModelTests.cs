using MiniDb.Database;
using MiniDb.Index;
using MiniDb.Records;
using MiniDb.Storage;

namespace MiniDb.Tests.Index;

/// <summary>
/// Random Insert, Replace, Delete and Find against a sorted dictionary as the model. Record sizes range from a few bytes
/// to nearly a full page, so leaves split, cells move, and an in-place overwrite falls back to delete and insert.
/// </summary>
public class ClusteredTreeModelTests
{
    private static int Operations =>
        int.TryParse(Environment.GetEnvironmentVariable("MINIDB_FUZZ_ITERATIONS"), out int value) ? value * 4 : 4000;

    [Fact]
    public void RandomOperations_MatchASortedDictionary()
    {
        var store = new MemoryPageStore();
        DatabaseFile.Create(store);
        var pool = new BufferPool(store, capacity: 512);
        ClusteredTree tree = ClusteredTree.Create(pool);
        var model = new SortedDictionary<long, byte[]>();
        var random = new Random(42);

        for (int i = 0; i < Operations; i++)
        {
            long key = random.Next(0, 1500);
            int choice = random.Next(10);
            if (choice < 4 && !model.ContainsKey(key))
            {
                byte[] record = RandomRecord(random);
                tree.Insert(key, record);
                model[key] = record;
            }
            else if (choice < 7 && model.ContainsKey(key))
            {
                byte[] record = RandomRecord(random);
                tree.Replace(key, record);
                model[key] = record;
            }
            else if (choice < 9)
            {
                Assert.Equal(model.Remove(key), tree.Delete(key));
            }
            else
            {
                Assert.Equal(model.GetValueOrDefault(key), tree.Find(key));
            }

            if (i % 500 == 499)
            {
                AssertSameContents(tree, model);
            }
        }

        AssertSameContents(tree, model);
    }

    /// <summary>
    /// Regression: a full leaf holds a 3000-byte and a 1000-byte record. A 3500-byte record goes between them. No split
    /// point puts two halves on one page, which used to throw InvalidOperationException from a valid INSERT.
    /// </summary>
    [Fact]
    public void InsertBetweenTwoLargeRecords_SplitsIntoThreePages_AndKeepsOrder()
    {
        var store = new MemoryPageStore();
        DatabaseFile.Create(store);
        ClusteredTree tree = ClusteredTree.Create(new BufferPool(store, capacity: 64));
        long rootBefore = tree.RootPage;
        var big = new byte[3000];
        var medium = new byte[3500];
        var small = new byte[1000];

        tree.Insert(1, big);
        tree.Insert(3, small);
        tree.Insert(2, medium);

        Assert.NotEqual(rootBefore, tree.RootPage);
        Assert.Equal(big, tree.Find(1));
        Assert.Equal(medium, tree.Find(2));
        Assert.Equal(small, tree.Find(3));
        Assert.Equal(new long[] { 1, 2, 3 }, tree.Range(0, 10).Select(cell => cell.Key).ToArray());
    }

    [Fact]
    public void Replace_WithALongerRecord_KeepsTheOtherKeys()
    {
        var store = new MemoryPageStore();
        DatabaseFile.Create(store);
        ClusteredTree tree = ClusteredTree.Create(new BufferPool(store, capacity: 256));
        for (long key = 0; key < 200; key++)
        {
            tree.Insert(key, new byte[40]);
        }

        var big = Enumerable.Repeat((byte)7, SlottedPage.MaxRecordSize - 8).ToArray();
        tree.Replace(100, big);

        Assert.Equal(big, tree.Find(100));
        Assert.Equal(new byte[40], tree.Find(99));
        Assert.Equal(new byte[40], tree.Find(101));
        Assert.Equal(200, tree.Range(0, 1000).Count());
    }

    private static byte[] RandomRecord(Random random)
    {
        int size = random.Next(10) == 0 ? random.Next(2000, SlottedPage.MaxRecordSize - 8) : random.Next(1, 120);
        var record = new byte[size];
        random.NextBytes(record);
        return record;
    }

    private static void AssertSameContents(ClusteredTree tree, SortedDictionary<long, byte[]> model)
    {
        var actual = tree.Range(long.MinValue, long.MaxValue).ToList();
        Assert.Equal(model.Count, actual.Count);
        int index = 0;
        foreach ((long key, byte[] record) in model)
        {
            Assert.Equal(key, actual[index].Key);
            Assert.Equal(record, actual[index].Record);
            index++;
        }
    }
}
