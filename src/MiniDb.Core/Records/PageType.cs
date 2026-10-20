namespace MiniDb.Records;

/// <summary>Kind of content a page holds. Stored in byte 0 of every non-header page.</summary>
public enum PageType : byte
{
    Catalog = 1,
    Leaf = 2,
    Internal = 3,
    Heap = 4,
    Free = 5,
}
