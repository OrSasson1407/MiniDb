using Microsoft.Win32.SafeHandles;

namespace MiniDb.Storage;

/// <summary>What the engine is about to do to a file. Raised just before the operation.</summary>
public enum FileEventKind
{
    Write,
    Extend,
    Truncate,
    Sync,
}

/// <summary>One file operation, as seen by a <see cref="FileObserver"/>.</summary>
public readonly struct FileEvent
{
    private readonly SafeFileHandle _handle;

    internal FileEvent(string filePath, FileEventKind kind, SafeFileHandle handle)
    {
        FilePath = filePath;
        Kind = kind;
        _handle = handle;
    }

    public string FilePath { get; }

    public FileEventKind Kind { get; }

    /// <summary>
    /// The file's current contents, read through the engine's own handle. The engine opens its files exclusively, so a
    /// test cannot open them itself.
    /// </summary>
    public byte[] ReadContents()
    {
        var bytes = new byte[RandomAccess.GetLength(_handle)];
        RandomAccess.Read(_handle, bytes, 0);
        return bytes;
    }
}

/// <summary>
/// Test seam for the file layer. A test on its own thread can watch every write and sync the engine makes, and can make
/// one fail by throwing (an I/O error), or record the file's contents at each sync (to model a power cut). The observer is
/// per thread, so the engine's own threads and other tests never see it.
/// </summary>
public static class FileObserver
{
    [ThreadStatic]
    private static Action<FileEvent>? s_observer;

    /// <summary>The observer for the calling thread, or null for none.</summary>
    public static Action<FileEvent>? Current
    {
        get => s_observer;
        set => s_observer = value;
    }

    internal static void Raise(string path, FileEventKind kind, SafeFileHandle handle) =>
        s_observer?.Invoke(new FileEvent(path, kind, handle));
}
