using Microsoft.Win32.SafeHandles;

namespace MiniDb.Storage;

/// <summary>Page store backed by a single file. Page <c>n</c> lives at byte offset <c>n * PageSize</c>.</summary>
public sealed class FilePageStore : IPageStore
{
    private readonly SafeFileHandle _handle;
    private readonly string _path;
    private long _pageCount;

    /// <param name="path">File to open.</param>
    /// <param name="create">True to create a new empty file (fails if it exists); false to open an existing one.</param>
    public FilePageStore(string path, bool create)
    {
        _path = path;
        _handle = File.OpenHandle(
            path,
            create ? FileMode.CreateNew : FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        _pageCount = RandomAccess.GetLength(_handle) / PageConstants.PageSize;
    }

    public long PageCount => _pageCount;

    public void ReadPage(long pageId, Span<byte> buffer)
    {
        CheckAllocated(pageId);
        int read = RandomAccess.Read(_handle, buffer[..PageConstants.PageSize], pageId * PageConstants.PageSize);
        if (read != PageConstants.PageSize)
        {
            throw new InvalidOperationException($"Short read on page {pageId}: {read} bytes.");
        }
    }

    public void WritePage(long pageId, ReadOnlySpan<byte> data)
    {
        CheckAllocated(pageId);
        FileObserver.Raise(_path, FileEventKind.Write, _handle);
        RandomAccess.Write(_handle, data[..PageConstants.PageSize], pageId * PageConstants.PageSize);
    }

    public long AllocatePage()
    {
        long pageId = _pageCount;
        _pageCount++;
        FileObserver.Raise(_path, FileEventKind.Extend, _handle);
        RandomAccess.SetLength(_handle, _pageCount * PageConstants.PageSize);
        return pageId;
    }

    public void Sync()
    {
        FileObserver.Raise(_path, FileEventKind.Sync, _handle);
        RandomAccess.FlushToDisk(_handle);
    }

    public void Dispose() => _handle.Dispose();

    private void CheckAllocated(long pageId)
    {
        if (pageId < 0 || pageId >= _pageCount)
        {
            throw new PageOutOfRangeException(pageId);
        }
    }
}
