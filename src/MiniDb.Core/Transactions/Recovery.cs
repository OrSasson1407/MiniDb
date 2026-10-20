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
            bool isPageChange = frame.Type is WalFrameType.PageImage or WalFrameType.PageDelta;
            if (isPageChange && committed.Contains(frame.TransactionId))
            {
                ApplyPageChange(store, frame);
            }
        }

        store.Sync();
        wal.Truncate();
    }

    private static void ApplyPageChange(IPageStore store, WalFrame frame)
    {
        // Pages allocated by a transaction that crashed are not in the log, so grow the file as needed.
        while (store.PageCount <= frame.PageId)
        {
            store.AllocatePage();
        }

        if (frame.Type == WalFrameType.PageImage)
        {
            store.WritePage(frame.PageId, frame.Payload);
            return;
        }

        // A delta applies to the page as the store holds it. Every change since the last checkpoint is in the log,
        // in order, so the store holds the base the delta was computed against.
        var page = new byte[PageConstants.PageSize];
        store.ReadPage(frame.PageId, page);
        PageDelta.Apply(page, frame.Payload);
        PageChecksum.Stamp(page);
        store.WritePage(frame.PageId, page);
    }
}
