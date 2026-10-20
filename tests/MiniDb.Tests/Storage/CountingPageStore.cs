using MiniDb.Storage;

namespace MiniDb.Tests.Storage;

/// <summary>Test double that wraps a page store and counts the calls that reach it.</summary>
public sealed class CountingPageStore : IPageStore
{
    private readonly IPageStore _inner;

    public CountingPageStore(IPageStore inner)
    {
        _inner = inner;
    }

    public int ReadCount { get; private set; }

    public int WriteCount { get; private set; }

    public long PageCount => _inner.PageCount;

    public void ReadPage(long pageId, Span<byte> buffer)
    {
        ReadCount++;
        _inner.ReadPage(pageId, buffer);
    }

    public void WritePage(long pageId, ReadOnlySpan<byte> data)
    {
        WriteCount++;
        _inner.WritePage(pageId, data);
    }

    public long AllocatePage() => _inner.AllocatePage();

    public void Sync() => _inner.Sync();

    public void Dispose() => _inner.Dispose();
}
