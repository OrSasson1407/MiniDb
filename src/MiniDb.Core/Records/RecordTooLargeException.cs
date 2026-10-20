namespace MiniDb.Records;

/// <summary>Raised when an encoded row cannot fit on one page.</summary>
public sealed class RecordTooLargeException : MiniDbException
{
    public RecordTooLargeException(int size)
        : base($"Record of {size} bytes exceeds the maximum of {SlottedPage.MaxRecordSize} bytes.")
    {
    }
}
