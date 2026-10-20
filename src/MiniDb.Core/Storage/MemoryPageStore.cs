namespace MiniDb.Storage;

/// <summary>In-memory page store used by tests and by components that do not need a file.</summary>
public sealed class MemoryPageStore : IPageStore
{
    private readonly List<byte[]> _pages = new();

    public long PageCount => _pages.Count;

    public void ReadPage(long pageId, Span<byte> buffer)
    {
        CheckAllocated(pageId);
        _pages[(int)pageId].AsSpan().CopyTo(buffer);
    }

    public void WritePage(long pageId, ReadOnlySpan<byte> data)
    {
        CheckAllocated(pageId);
        CheckPageSize(data.Length);
        data.CopyTo(_pages[(int)pageId]);
    }

    public long AllocatePage()
    {
        _pages.Add(new byte[PageConstants.PageSize]);
        return _pages.Count - 1;
    }

    public void Sync()
    {
    }

    public void Dispose()
    {
    }

    private void CheckAllocated(long pageId)
    {
        if (pageId < 0 || pageId >= _pages.Count)
        {
            throw new PageOutOfRangeException(pageId);
        }
    }

    private static void CheckPageSize(int length)
    {
        if (length != PageConstants.PageSize)
        {
            throw new ArgumentException($"A page must be {PageConstants.PageSize} bytes, got {length}.");
        }
    }
}
