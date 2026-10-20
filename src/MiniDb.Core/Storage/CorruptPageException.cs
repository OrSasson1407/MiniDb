namespace MiniDb.Storage;

/// <summary>Raised when a page read from the data file fails its checksum.</summary>
public sealed class CorruptPageException : MiniDbException
{
    public CorruptPageException(long pageId)
        : base($"Page {pageId} failed its checksum; the database file is damaged.")
    {
    }
}
