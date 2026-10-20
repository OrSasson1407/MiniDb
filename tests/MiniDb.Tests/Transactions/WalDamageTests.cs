using MiniDb.Database;
using MiniDb.Storage;
using MiniDb.Transactions;

namespace MiniDb.Tests.Transactions;

/// <summary>
/// Damage inside the log is different from a torn tail. A torn tail is the last write before a crash and is cut off.
/// Damage with valid records after it means committed work would be lost silently, so recovery must refuse.
/// </summary>
public class WalDamageTests : IDisposable
{
    private readonly string _walPath = Path.Combine(Path.GetTempPath(), $"minidb-{Guid.NewGuid():N}.wal");

    public void Dispose()
    {
        File.Delete(_walPath);
    }

    private static byte[] PageWith(byte value)
    {
        var data = new byte[PageConstants.PageSize];
        data[0] = value;
        return data;
    }

    [Fact]
    public void DamageInsideTheLog_WithValidRecordsAfterIt_IsReported_NotSkipped()
    {
        using (var wal = new WriteAheadLog(_walPath))
        {
            wal.Append(new WalFrame(1, WalFrameType.PageImage, 1, PageWith(1)));
            wal.Append(new WalFrame(1, WalFrameType.Commit, 0, Array.Empty<byte>()));
            wal.Append(new WalFrame(2, WalFrameType.PageImage, 2, PageWith(2)));
            wal.Append(new WalFrame(2, WalFrameType.Commit, 0, Array.Empty<byte>()));
            wal.Sync();
        }

        FlipByteAt(20);

        using var reopened = new WriteAheadLog(_walPath);
        Assert.Throws<CorruptLogException>(() => reopened.ReadAll().ToList());
    }

    [Fact]
    public void DamageInTheLastRecord_IsATornTail_AndTheEarlierRecordsAreKept()
    {
        using (var wal = new WriteAheadLog(_walPath))
        {
            wal.Append(new WalFrame(1, WalFrameType.PageImage, 1, PageWith(1)));
            wal.Append(new WalFrame(1, WalFrameType.Commit, 0, Array.Empty<byte>()));
            wal.Append(new WalFrame(2, WalFrameType.PageImage, 2, PageWith(2)));
            wal.Sync();
        }

        FlipByteAt(new FileInfo(_walPath).Length - 3);

        using var reopened = new WriteAheadLog(_walPath);
        List<WalFrame> frames = reopened.ReadAll().ToList();
        Assert.Equal(2, frames.Count);
        Assert.Equal(WalFrameType.Commit, frames[1].Type);
    }

    [Fact]
    public void Recovery_RefusesADamagedLog_AndLeavesTheDataFileAlone()
    {
        var store = new MemoryPageStore();
        DatabaseFile.Create(store);
        store.AllocatePage();
        using (var wal = new WriteAheadLog(_walPath))
        {
            wal.Append(new WalFrame(1, WalFrameType.PageImage, 1, PageWith(0x11)));
            wal.Append(new WalFrame(1, WalFrameType.Commit, 0, Array.Empty<byte>()));
            wal.Append(new WalFrame(2, WalFrameType.PageImage, 1, PageWith(0x22)));
            wal.Append(new WalFrame(2, WalFrameType.Commit, 0, Array.Empty<byte>()));
            wal.Sync();
        }

        FlipByteAt(20);

        using var reopened = new WriteAheadLog(_walPath);
        Assert.Throws<CorruptLogException>(() => Recovery.Run(store, reopened));
        var page = new byte[PageConstants.PageSize];
        store.ReadPage(1, page);
        Assert.Equal(0, page[0]);
        Assert.True(File.Exists(_walPath), "the damaged log must be kept for inspection");
    }

    private void FlipByteAt(long offset)
    {
        using var stream = new FileStream(_walPath, FileMode.Open, FileAccess.ReadWrite);
        stream.Position = offset;
        int value = stream.ReadByte();
        stream.Position = offset;
        stream.WriteByte((byte)(value ^ 0xFF));
    }
}
