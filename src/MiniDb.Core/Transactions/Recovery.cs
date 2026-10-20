using MiniDb.Storage;

namespace MiniDb.Transactions;

/// <summary>
/// Brings the data file up to date after a crash: replays the page images of committed transactions,
/// in log order, and ignores the images of transactions that never committed. Then truncates the log.
/// Must run before any pool or transaction manager is created on the same files.
/// </summary>
public static class Recovery
{
    public static void Run(IPageStore store, WriteAheadLog wal)
    {
        List<WalFrame> frames = wal.ReadAll().ToList();
        HashSet<long> committed = frames
            .Where(frame => frame.Type == WalFrameType.Commit)
            .Select(frame => frame.TransactionId)
            .ToHashSet();

        foreach (WalFrame frame in frames)
        {
            if (frame.Type == WalFrameType.PageImage && committed.Contains(frame.TransactionId))
            {
                ApplyPageImage(store, frame);
            }
        }

        store.Sync();
        wal.Truncate();
    }

    private static void ApplyPageImage(IPageStore store, WalFrame frame)
    {
        // Pages allocated by a transaction that crashed are not in the log, so grow the file as needed.
        while (store.PageCount <= frame.PageId)
        {
            store.AllocatePage();
        }

        store.WritePage(frame.PageId, frame.Payload);
    }
}
