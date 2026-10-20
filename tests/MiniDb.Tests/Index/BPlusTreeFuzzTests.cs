using MiniDb.Index;
using MiniDb.Storage;

namespace MiniDb.Tests.Index;

public class BPlusTreeFuzzTests
{
    [Fact]
    public void RandomOperations_MatchSortedDictionaryModel()
    {
        var random = new Random(42);
        var pool = new BufferPool(BPlusTreeTests.NewStore(), capacity: 32);
        var tree = BPlusTree.Create(pool);
        var model = new SortedDictionary<long, RowLocation>();
        long[] specialKeys = { long.MinValue, -1, 0, 1, long.MaxValue };

        for (int step = 0; step < 5000; step++)
        {
            long key = random.Next(4) == 0
                ? specialKeys[random.Next(specialKeys.Length)]
                : random.NextInt64(-1000, 1000);
            int operation = random.Next(3);

            if (step % 100 == 99)
            {
                pool.FlushAll();
            }

            if (operation == 0)
            {
                var location = new RowLocation(step, step % 7);
                if (model.ContainsKey(key))
                {
                    Assert.Throws<DuplicateKeyException>(() => tree.Insert(key, location));
                }
                else
                {
                    tree.Insert(key, location);
                    model[key] = location;
                }
            }
            else if (operation == 1)
            {
                Assert.Equal(model.Remove(key), tree.Delete(key));
            }

            Assert.Equal(model.TryGetValue(key, out RowLocation expected) ? expected : (RowLocation?)null, tree.Find(key));
        }

        var all = tree.Range(long.MinValue, long.MaxValue).Select(e => (e.Key.Major, e.Location)).ToList();
        Assert.Equal(model.Select(pair => (pair.Key, pair.Value)), all);
    }
}
