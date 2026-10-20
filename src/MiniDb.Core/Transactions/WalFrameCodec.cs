using System.Buffers.Binary;
using MiniDb.Storage;

namespace MiniDb.Transactions;

/// <summary>
/// Byte format of one WAL record: transactionId 8, type 1, pageId 8, payloadLength 4, payload, checksum 4.
/// The checksum (CRC-32) covers every byte before it, so a torn or damaged record is detected.
/// </summary>
public static class WalFrameCodec
{
    private const int HeaderSize = 8 + 1 + 8 + 4;
    private const int ChecksumSize = sizeof(uint);

    /// <summary>Largest payload: a full page image, or a delta of up to one page's worth of segments.</summary>
    private const int MaxPayloadBytes = PageConstants.PageSize * 2;

    public static byte[] Encode(WalFrame frame)
    {
        ValidatePayload(frame);
        int bodySize = HeaderSize + frame.Payload.Length;
        var bytes = new byte[bodySize + ChecksumSize];

        BinaryPrimitives.WriteInt64LittleEndian(bytes, frame.TransactionId);
        bytes[8] = (byte)frame.Type;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(9), frame.PageId);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(17), frame.Payload.Length);
        frame.Payload.CopyTo(bytes, HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bodySize), Checksum.Compute(bytes.AsSpan(0, bodySize)));
        return bytes;
    }

    /// <summary>
    /// Reads one record from the current position. Returns false, without throwing, when the stream ends
    /// early or the record fails validation, which is how a torn tail after a crash looks.
    /// </summary>
    public static bool TryDecode(Stream stream, out WalFrame frame)
    {
        frame = null!;
        var header = new byte[HeaderSize];
        if (!ReadExactly(stream, header))
        {
            return false;
        }

        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(17));
        if (payloadLength < 0 || payloadLength > MaxPayloadBytes || !IsKnownType(header[8]))
        {
            return false;
        }

        var body = new byte[HeaderSize + payloadLength + ChecksumSize];
        header.CopyTo(body, 0);
        if (!ReadExactly(stream, body.AsSpan(HeaderSize)))
        {
            return false;
        }

        int bodySize = HeaderSize + payloadLength;
        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(bodySize));
        if (Checksum.Compute(body.AsSpan(0, bodySize)) != stored)
        {
            return false;
        }

        frame = new WalFrame(
            BinaryPrimitives.ReadInt64LittleEndian(body),
            (WalFrameType)body[8],
            BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(9)),
            body.AsSpan(HeaderSize, payloadLength).ToArray());
        return true;
    }

    /// <summary>
    /// True when <paramref name="bytes"/> starts with a complete record whose checksum matches. Allocates nothing,
    /// so it can scan a damaged log for records that survived.
    /// </summary>
    public static bool StartsWithValidRecord(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize)
        {
            return false;
        }

        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[17..]);
        if (payloadLength < 0 || payloadLength > MaxPayloadBytes || !IsKnownType(bytes[8]))
        {
            return false;
        }

        int bodySize = HeaderSize + payloadLength;
        if (bytes.Length < bodySize + ChecksumSize)
        {
            return false;
        }

        return Checksum.Compute(bytes[..bodySize]) == BinaryPrimitives.ReadUInt32LittleEndian(bytes[bodySize..]);
    }

    private static void ValidatePayload(WalFrame frame)
    {
        if (frame.Type == WalFrameType.PageImage && frame.Payload.Length != PageConstants.PageSize)
        {
            throw new ArgumentException($"A PageImage payload must be {PageConstants.PageSize} bytes.", nameof(frame));
        }

        if (frame.Type == WalFrameType.PageDelta && frame.Payload.Length > MaxPayloadBytes)
        {
            throw new ArgumentException("A PageDelta payload is too large.", nameof(frame));
        }

        if (frame.Type == WalFrameType.Commit && frame.Payload.Length != 0)
        {
            throw new ArgumentException("A Commit frame carries no payload.", nameof(frame));
        }
    }

    private static bool IsKnownType(byte type) =>
        type is (byte)WalFrameType.PageImage or (byte)WalFrameType.Commit or (byte)WalFrameType.PageDelta;

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }
}
