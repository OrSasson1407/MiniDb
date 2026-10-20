namespace MiniDb.Storage;

/// <summary>Layout constants shared by every page-based component.</summary>
public static class PageConstants
{
    /// <summary>Size in bytes of every page in the data file.</summary>
    public const int PageSize = 4096;

    /// <summary>Bytes at the end of every page that hold its CRC-32.</summary>
    public const int ChecksumSize = 4;

    /// <summary>Bytes available to page contents. The last ChecksumSize bytes belong to the checksum.</summary>
    public const int UsableSize = PageSize - ChecksumSize;
}
