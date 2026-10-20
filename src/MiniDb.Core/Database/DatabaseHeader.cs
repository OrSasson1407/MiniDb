namespace MiniDb.Database;

/// <summary>Contents of page 0: the file-level facts every other component reads first.</summary>
/// <param name="FormatVersion">On-disk format version, starting at 1.</param>
/// <param name="PageCount">Number of pages in the file, including page 0.</param>
/// <param name="CatalogRootPage">First page of the table catalog; 0 when no table exists yet (page 0 is never a catalog page).</param>
/// <param name="FreeListHead">First free page; 0 when there are no free pages.</param>
public sealed record DatabaseHeader(int FormatVersion, long PageCount, long CatalogRootPage, long FreeListHead);
