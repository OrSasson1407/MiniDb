namespace MiniDb.Api;

/// <summary>Tuning knobs for <see cref="MiniDatabase.Open"/>. The defaults are reasonable for a small database.</summary>
public sealed class DatabaseOptions
{
    /// <summary>Number of 4 KB pages kept in memory. 256 pages is 1 MB.</summary>
    public int BufferPoolPages { get; init; } = 256;

    /// <summary>When true, every commit is forced to disk before it returns. Turn off only for measurements.</summary>
    public bool FsyncOnCommit { get; init; } = true;

    /// <summary>The WAL is checkpointed into the data file once it grows past this size.</summary>
    public long CheckpointThresholdBytes { get; init; } = 4 * 1024 * 1024;
}
