using MiniDb.Storage;
using MiniDb.Transactions;

namespace MiniDb.Tests.Transactions;

public class WalFrameCodecTests
{
    [Fact]
    public void PageImageFrame_RoundTrips()
    {
        var payload = new byte[PageConstants.PageSize];
        payload[10] = 42;
        var frame = new WalFrame(TransactionId: 5, WalFrameType.PageImage, PageId: 3, payload);

        using var ms = new MemoryStream(WalFrameCodec.Encode(frame));
        Assert.True(WalFrameCodec.TryDecode(ms, out var read));

        Assert.Equal(frame.TransactionId, read.TransactionId);
        Assert.Equal(frame.PageId, read.PageId);
        Assert.Equal(frame.Payload, read.Payload);
    }

    [Fact]
    public void CommitFrame_RoundTrips_WithEmptyPayload()
    {
        var frame = new WalFrame(TransactionId: 9, WalFrameType.Commit, PageId: 0, Array.Empty<byte>());

        using var ms = new MemoryStream(WalFrameCodec.Encode(frame));

        Assert.True(WalFrameCodec.TryDecode(ms, out var read));
        Assert.Equal(WalFrameType.Commit, read.Type);
        Assert.Empty(read.Payload);
    }

    [Fact]
    public void FlippedByte_FailsChecksum_AndReturnsFalse()
    {
        var encoded = WalFrameCodec.Encode(new WalFrame(1, WalFrameType.Commit, 0, Array.Empty<byte>()));
        encoded[3] ^= 0xFF;

        using var ms = new MemoryStream(encoded);

        Assert.False(WalFrameCodec.TryDecode(ms, out _));
    }

    [Fact]
    public void TruncatedFrame_ReturnsFalse_WithoutThrowing()
    {
        var encoded = WalFrameCodec.Encode(new WalFrame(1, WalFrameType.Commit, 0, Array.Empty<byte>()));

        using var ms = new MemoryStream(encoded, 0, encoded.Length - 2);

        Assert.False(WalFrameCodec.TryDecode(ms, out _));
    }

    [Fact]
    public void PageImageWithWrongSize_Throws()
    {
        var frame = new WalFrame(1, WalFrameType.PageImage, 1, new byte[10]);

        Assert.Throws<ArgumentException>(() => WalFrameCodec.Encode(frame));
    }
}
