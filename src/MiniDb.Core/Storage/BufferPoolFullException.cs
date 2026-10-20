namespace MiniDb.Storage;

/// <summary>
/// Raised when the pool has grown to its limit and every frame is still pinned or holds an uncommitted change:
/// the transaction is too large for the memory the pool may use, or pages are pinned and never released.
/// </summary>
public sealed class BufferPoolFullException : MiniDbException
{
    public BufferPoolFullException(int capacity)
        : base($"Buffer pool of {capacity} pages (and its overflow limit) has no clean, unpinned page to reuse.")
    {
    }
}
