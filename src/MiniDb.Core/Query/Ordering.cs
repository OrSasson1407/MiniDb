using MiniDb.Records;

namespace MiniDb.Query;

/// <summary>One sort key: a column and its direction.</summary>
internal readonly record struct SortKey(ColumnRef Column, bool Descending);

/// <summary>
/// Orders joined tuples. With a LIMIT only the first <c>limit</c> rows are kept, in a bounded heap, so a query such as
/// <c>ORDER BY x LIMIT 10</c> never holds more than ten rows in the sort.
/// </summary>
internal static class Ordering
{
    public static Comparison<RowView[]> ComparerFor(IReadOnlyList<SortKey> keys) => (left, right) =>
    {
        foreach (SortKey key in keys)
        {
            int order = Comparison.CompareCells(left[key.Column.Table], right[key.Column.Table], key.Column);
            if (order != 0)
            {
                return key.Descending ? -order : order;
            }
        }

        return 0;
    };

    /// <summary>The first <paramref name="limit"/> tuples in sort order.</summary>
    public static List<RowView[]> FirstInOrder(IEnumerable<RowView[]> rows, IReadOnlyList<SortKey> keys, int limit)
    {
        Comparison<RowView[]> compare = ComparerFor(keys);
        if (limit <= 0)
        {
            return new List<RowView[]>();
        }

        if (limit == int.MaxValue)
        {
            var all = rows.ToList();
            all.Sort(compare);
            return all;
        }

        // The heap's top is the worst row kept so far; a better row replaces it.
        var heap = new PriorityQueue<RowView[], RowView[]>(Comparer<RowView[]>.Create((x, y) => compare(y, x)));
        foreach (RowView[] row in rows)
        {
            if (heap.Count < limit)
            {
                heap.Enqueue(row, row);
            }
            else if (compare(row, heap.Peek()) < 0)
            {
                heap.Dequeue();
                heap.Enqueue(row, row);
            }
        }

        var result = new List<RowView[]>(heap.Count);
        while (heap.Count > 0)
        {
            result.Add(heap.Dequeue());
        }

        result.Reverse();
        return result;
    }
}
