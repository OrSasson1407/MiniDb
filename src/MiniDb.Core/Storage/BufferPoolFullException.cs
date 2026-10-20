namespace MiniDb.Storage;

/// <summary>Raised when every frame is pinned or dirty, so no page can be loaded without losing data.</summary>
public sealed class BufferPoolFullException : MiniDbException
{
    public BufferPoolFullException(int capacity)
        : base($"Buffer pool of {capacity} pages has no clean, unpinned page to reuse.")
    {
    }
}
