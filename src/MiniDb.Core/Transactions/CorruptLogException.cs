namespace MiniDb.Transactions;

/// <summary>
/// The write-ahead log is damaged in the middle: a record fails validation, but valid records follow it. Recovering
/// would silently drop the committed transactions after the damage, so the database refuses to open instead.
/// The log is kept on disk so that it can be inspected.
/// </summary>
public sealed class CorruptLogException : MiniDbException
{
    public CorruptLogException(long offset)
        : base($"The write-ahead log is damaged at byte {offset}, and valid records follow it. The database was not recovered.")
    {
        Offset = offset;
    }

    /// <summary>Byte offset of the first damaged record.</summary>
    public long Offset { get; }
}
