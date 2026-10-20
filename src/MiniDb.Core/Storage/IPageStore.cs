namespace MiniDb.Storage;

/// <summary>Reads and writes fixed-size pages. The only component that touches the disk for data pages.</summary>
public interface IPageStore : IDisposable
{
    /// <summary>Number of pages currently allocated.</summary>
    long PageCount { get; }

    /// <summary>Copies page <paramref name="pageId"/> into <paramref name="buffer"/> (PageSize bytes).</summary>
    void ReadPage(long pageId, Span<byte> buffer);

    /// <summary>Overwrites page <paramref name="pageId"/> with <paramref name="data"/> (PageSize bytes).</summary>
    void WritePage(long pageId, ReadOnlySpan<byte> data);

    /// <summary>Appends a zero-filled page and returns its id.</summary>
    long AllocatePage();

    /// <summary>Forces written pages to durable storage.</summary>
    void Sync();
}
