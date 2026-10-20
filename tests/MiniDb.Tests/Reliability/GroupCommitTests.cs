using MiniDb.Api;
using MiniDb.Storage;

namespace MiniDb.Tests.Reliability;

/// <summary>
/// Group commit lets several writers share one log sync. These tests check what must hold however the syncs are shared:
/// every commit that returned survives a crash, and a failed sync stops the engine instead of letting it go on.
/// </summary>
public class GroupCommitTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("minidb-group-").FullName;

    private string DataPath => Path.Combine(_directory, "group.mdb");

    public void Dispose()
    {
        FileObserver.Current = null;
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task ConcurrentWriters_EveryAcknowledgedCommit_SurvivesACrash()
    {
        MiniDatabase db = Open();
        db.Execute("CREATE TABLE events (id BIGINT PRIMARY KEY, writer INT)");
        Table events = db.GetTable("events");
        var acknowledged = new System.Collections.Concurrent.ConcurrentBag<long>();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
        {
            for (int i = 0; i < 150; i++)
            {
                long id = writer * 10_000L + i;
                events.Insert(new object?[] { id, writer });
                acknowledged.Add(id);
            }
        }))).WaitAsync(TimeSpan.FromMinutes(2));

        db.SimulateCrash();

        using MiniDatabase reopened = Open();
        var present = reopened.GetTable("events").Scan().Select(row => (long)row[0]!).ToHashSet();
        foreach (long id in acknowledged)
        {
            Assert.True(present.Contains(id), $"acknowledged commit {id} was lost");
        }

        Assert.Equal(8 * 150, present.Count);
    }

    [Fact]
    public void FailedSync_IsReported_AndTheEngineStopsAcceptingWork()
    {
        MiniDatabase db = Open();
        db.Execute("CREATE TABLE events (id BIGINT PRIMARY KEY, writer INT)");
        Table events = db.GetTable("events");
        events.Insert(new object?[] { 1L, 0 });

        FileObserver.Current = e =>
        {
            if (e.Kind == FileEventKind.Sync && e.FilePath.EndsWith(".wal", StringComparison.Ordinal))
            {
                throw new IOException("injected sync failure");
            }
        };

        Assert.Throws<IOException>(() => events.Insert(new object?[] { 2L, 0 }));
        FileObserver.Current = null;

        Assert.Throws<IOException>(() => db.BeginTransaction());
        db.SimulateCrash();

        using MiniDatabase reopened = Open();
        var present = reopened.GetTable("events").Scan().Select(row => (long)row[0]!).ToHashSet();
        Assert.Contains(1L, present);
    }

    private MiniDatabase Open() => MiniDatabase.Open(DataPath);
}
