using Microsoft.Win32.SafeHandles;

namespace MiniDb.Transactions;

/// <summary>Append-only log file of <see cref="WalFrame"/> records. Writes are durable only after <see cref="Sync"/>.</summary>
public sealed class WriteAheadLog : IDisposable
{
    private readonly SafeFileHandle _handle;
    private long _length;

    public WriteAheadLog(string path)
    {
        _handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _length = RandomAccess.GetLength(_handle);
    }

    public void Append(WalFrame frame)
    {
        byte[] bytes = WalFrameCodec.Encode(frame);
        RandomAccess.Write(_handle, bytes, _length);
        _length += bytes.Length;
    }

    /// <summary>Current size of the log file in bytes.</summary>
    public long Length => _length;

    public void Sync() => RandomAccess.FlushToDisk(_handle);

    /// <summary>Reads every complete, valid record from the start. Stops silently at the first damaged one.</summary>
    public IEnumerable<WalFrame> ReadAll()
    {
        var bytes = new byte[_length];
        RandomAccess.Read(_handle, bytes, 0);
        using var stream = new MemoryStream(bytes, writable: false);
        while (WalFrameCodec.TryDecode(stream, out WalFrame frame))
        {
            yield return frame;
        }
    }

    /// <summary>Cuts the log back to <paramref name="length"/> bytes, discarding a partly written commit.</summary>
    public void TruncateTo(long length)
    {
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
