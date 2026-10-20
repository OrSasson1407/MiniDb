using MiniDb.Api;
using MiniDb.Catalog;
using MiniDb.Index;
using MiniDb.Records;

namespace MiniDb.Query;

/// <summary>Reads the rows of one table that an access plan names. SELECT, UPDATE and DELETE all start here.</summary>
internal static class AccessPaths
{
    /// <summary>Largest number of index entries a secondary index may return and still be used.</summary>
    public const int SelectivityLimit = 1024;

    /// <summary>The planner's choice, except that a secondary index is dropped when it would return too many rows.</summary>
    public static AccessPlan ChoosePlan(MiniDatabase db, TableInfo table, Expr? where)
    {
        AccessPlan plan = Planner.Plan(table.Schema, where, table.SecondaryIndexRoots.Keys);
        if (plan is IndexRangePlan index && !IsSelective(db, table, index, SelectivityLimit))
        {
            return new FullScanPlan();
        }

        return plan;
    }

    /// <summary>The rows the plan reads. The caller still applies the whole WHERE clause.</summary>
    public static IEnumerable<RowView> Rows(MiniDatabase db, TableInfo table, AccessPlan plan) => plan switch
    {
        PointLookupPlan point => Single(RowStore.GetView(db.Pool, table, point.Key)),
        KeyRangePlan range => RangeRows(db, table, range),
        IndexRangePlan index => IndexRows(db, table, index),
        _ => RowStore.ScanRecords(db.Pool, table).Select(record => new RowView(table.Schema, record)),
    };

    /// <summary>The index entries of a range, without reading the rows they point to.</summary>
    public static IEnumerable<TreeKey> IndexEntries(MiniDatabase db, TableInfo table, IndexRangePlan range)
    {
        var tree = BPlusTree.Open(db.Pool, table.SecondaryIndexRoots[range.Column]);
        foreach ((TreeKey key, _) in tree.Range(new TreeKey(range.From, long.MinValue), new TreeKey(range.To, long.MaxValue)))
        {
            yield return key;
        }
    }

    /// <summary>True when the index range holds at most <paramref name="limit"/> entries.</summary>
    private static bool IsSelective(MiniDatabase db, TableInfo table, IndexRangePlan range, int limit)
    {
        var tree = BPlusTree.Open(db.Pool, table.SecondaryIndexRoots[range.Column]);
        int count = 0;
        foreach (var _ in tree.Range(new TreeKey(range.From, long.MinValue), new TreeKey(range.To, long.MaxValue)))
        {
            if (++count > limit)
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<RowView> IndexRows(MiniDatabase db, TableInfo table, IndexRangePlan range)
    {
        foreach (TreeKey key in IndexEntries(db, table, range))
        {
            RowView? view = RowStore.GetView(db.Pool, table, key.Minor);
            if (view is not null)
            {
                yield return view.Value;
            }
        }
    }

    private static IEnumerable<RowView> RangeRows(MiniDatabase db, TableInfo table, KeyRangePlan range)
    {
        foreach ((_, byte[] record) in ClusteredTree.Open(db.Pool, table.PrimaryKeyRoot).Range(range.From, range.To))
        {
            yield return new RowView(table.Schema, record);
        }
    }

    private static IEnumerable<RowView> Single(RowView? view) =>
        view is null ? Array.Empty<RowView>() : new[] { view.Value };
}
