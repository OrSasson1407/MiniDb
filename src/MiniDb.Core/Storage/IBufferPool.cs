namespace MiniDb.Storage;

/// <summary>Fixed-size cache of pages between the engine and the page store.</summary>
public interface IBufferPool : IDisposable
{
    /// <summary>Maximum number of pages held at once.</summary>
    int Capacity { get; }

    /// <summary>Loads page <paramref name="pageId"/> if needed and pins it until the result is disposed.</summary>
    PinnedPage Pin(long pageId);

    /// <summary>Writes every dirty page to the store and clears the dirty flags.</summary>
    void FlushAll();

    /// <summary>Drops a page from the pool so its next Pin re-reads the store. Unflushed changes are lost.</summary>
    void Discard(long pageId);
}
