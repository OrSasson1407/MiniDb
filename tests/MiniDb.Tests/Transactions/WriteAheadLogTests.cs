using MiniDb.Storage;
using MiniDb.Transactions;

namespace MiniDb.Tests.Transactions;

public class WriteAheadLogTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"minidb-{Guid.NewGuid():N}.wal");

    private static WalFrame Commit(long transactionId) =>
        new(transactionId, WalFrameType.Commit, 0, Array.Empty<byte>());

    [Fact]
    public void AppendedFrames_SurviveReopen_InOrder()
    {
        string path = TempPath();
        try
        {
            using (var wal = new WriteAheadLog(path))
            {
                wal.Append(Commit(1));
                wal.Append(Commit(2));
                wal.Append(Commit(3));
                wal.Sync();
            }

            using var reopened = new WriteAheadLog(path);

            Assert.Equal(new long[] { 1, 2, 3 }, reopened.ReadAll().Select(f => f.TransactionId));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TornTailAfterValidFrames_IsIgnored()
    {
        string path = TempPath();
        try
        {
            using (var wal = new WriteAheadLog(path))
            {
                wal.Append(Commit(1));
                wal.Append(Commit(2));
            }
            using (var raw = new FileStream(path, FileMode.Append, FileAccess.Write))
            {
                raw.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
            }

            using var reopened = new WriteAheadLog(path);

            Assert.Equal(new long[] { 1, 2 }, reopened.ReadAll().Select(f => f.TransactionId));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Truncate_EmptiesTheLog()
    {
        string path = TempPath();
        try
        {
            using var wal = new WriteAheadLog(path);
            wal.Append(Commit(1));

            wal.Truncate();

            Assert.Empty(wal.ReadAll());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
