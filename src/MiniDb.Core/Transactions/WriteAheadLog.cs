using Microsoft.Win32.SafeHandles;
using MiniDb.Storage;

namespace MiniDb.Transactions;

/// <summary>Append-only log file of <see cref="WalFrame"/> records. Writes are durable only after <see cref="Sync"/>.</summary>
public sealed class WriteAheadLog : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly string _path;
    private long _length;

    public WriteAheadLog(string path)
    {
        _path = path;
        _handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _length = RandomAccess.GetLength(_handle);
    }

    public void Append(WalFrame frame)
    {
        byte[] bytes = WalFrameCodec.Encode(frame);
        FileObserver.Raise(_path, FileEventKind.Write, _handle);
        RandomAccess.Write(_handle, bytes, _length);
        _length += bytes.Length;
    }

    /// <summary>Current size of the log file in bytes.</summary>
    public long Length => _length;

    public void Sync()
    {
        FileObserver.Raise(_path, FileEventKind.Sync, _handle);
        RandomAccess.FlushToDisk(_handle);
    }

    /// <summary>
    /// Reads every complete, valid record from the start. A damaged record at the very end is a torn write and ends the
    /// log. A damaged record with valid records after it throws <see cref="CorruptLogException"/>: skipping it would drop
    /// committed transactions without a trace.
    /// </summary>
    public IEnumerable<WalFrame> ReadAll()
    {
        var bytes = new byte[_length];
        RandomAccess.Read(_handle, bytes, 0);
        int offset = 0;
        while (offset < bytes.Length)
        {
            using var stream = new MemoryStream(bytes, offset, bytes.Length - offset, writable: false);
            if (!WalFrameCodec.TryDecode(stream, out WalFrame frame))
            {
                break;
            }

            yield return frame;
            offset += (int)stream.Position;
        }

        if (offset < bytes.Length && HasValidRecordAfter(bytes, offset))
        {
            throw new CorruptLogException(offset);
        }
    }

    private static bool HasValidRecordAfter(byte[] bytes, int damagedAt)
    {
        for (int position = damagedAt + 1; position < bytes.Length; position++)
        {
            if (WalFrameCodec.StartsWithValidRecord(bytes.AsSpan(position)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Cuts the log back to <paramref name="length"/> bytes, discarding a partly written commit.</summary>
    public void TruncateTo(long length)
    {
        FileObserver.Raise(_path, FileEventKind.Truncate, _handle);
        RandomAccess.SetLength(_handle, length);
        _length = length;
    }

    /// <summary>Empties the log. Called after a checkpoint has copied every committed page to the data file.</summary>
    public void Truncate()
    {
        TruncateTo(0);
        Sync();
    }

    public void Dispose() => _handle.Dispose();
}
