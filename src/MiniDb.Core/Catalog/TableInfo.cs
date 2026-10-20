using MiniDb.Records;

namespace MiniDb.Catalog;

/// <summary>Everything the engine needs to find a table's rows: its schema and the root pages of its indexes.</summary>
/// <param name="Schema">Columns and primary key.</param>
/// <param name="PrimaryKeyRoot">Root page of the primary-key B+ tree.</param>
/// <param name="SecondaryIndexRoots">Root page of each secondary index, keyed by column name.</param>
/// <param name="HeapRoot">First heap page holding the rows.</param>
/// <param name="HeapTail">Last heap page; new rows are appended there.</param>
public sealed record TableInfo(
    TableSchema Schema,
    long PrimaryKeyRoot,
    IReadOnlyDictionary<string, long> SecondaryIndexRoots,
    long HeapRoot = 0,
    long HeapTail = 0);
