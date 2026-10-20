namespace MiniDb.Storage;

/// <summary>Raised when a page id is outside the pages the store has allocated.</summary>
public sealed class PageOutOfRangeException : MiniDbException
{
    public PageOutOfRangeException(long pageId)
        : base($"Page {pageId} is not allocated.")
    {
    }
}
