namespace MiniDb.Transactions;

public enum WalFrameType : byte
{
    /// <summary>Full after-image of one page, written by a transaction.</summary>
    PageImage = 1,

    /// <summary>Marks the transaction as committed. Carries no payload.</summary>
    Commit = 2,
}

/// <summary>One record of the write-ahead log.</summary>
/// <param name="TransactionId">Transaction that wrote this record.</param>
/// <param name="Type">PageImage or Commit.</param>
/// <param name="PageId">Page the image belongs to; 0 for Commit.</param>
/// <param name="Payload">PageSize bytes for PageImage, empty for Commit.</param>
public sealed record WalFrame(long TransactionId, WalFrameType Type, long PageId, byte[] Payload);
