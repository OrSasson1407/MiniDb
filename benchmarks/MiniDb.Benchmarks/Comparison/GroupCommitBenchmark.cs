using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using MiniDb.Api;

namespace MiniDb.Benchmarks.Comparison;

/// <summary>
/// Durable commits from several writers at once. Each commit is one autocommitted row, so every commit must reach the disk
/// before it is reported. The total is fixed, so more writers means each does fewer commits. Group commit shows here as
/// writers sharing one sync. SQLite runs in WAL mode with synchronous=FULL, one connection per writer.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 5, invocationCount: 1)]
public class GroupCommitBenchmark
{
    private const int TotalCommits = 2_000;

    [Params(1, 2, 4, 8)]
    public int Writers { get; set; }

    private string _directory = string.Empty;
    private MiniDatabase _db = null!;
    private Table _events = null!;
    private List<SqliteConnection> _connections = null!;

    [IterationSetup]
    public void Setup()
    {
        _directory = Directory.CreateTempSubdirectory("bench-group-").FullName;
        _db = MiniDatabase.Open(Path.Combine(_directory, "group.mdb"));
        _db.Execute("CREATE TABLE events (id BIGINT PRIMARY KEY, writer INT)");
        _events = _db.GetTable("events");

        string sqlitePath = Path.Combine(_directory, "group.sqlite");
        _connections = Enumerable.Range(0, Writers).Select(_ => OpenSqlite(sqlitePath)).ToList();
    }

    [IterationCleanup]
    public void Cleanup()
    {
        foreach (SqliteConnection connection in _connections)
        {
            connection.Dispose();
        }

        _db.Dispose();
        BenchmarkData.DeleteDirectory(_directory);
    }

    [Benchmark(Baseline = true)]
    public void MiniDbCommits() => RunWriters(writer =>
    {
        for (int i = 0; i < PerWriter; i++)
        {
            _events.Insert(new object?[] { writer * 1_000_000L + i, writer });
        }
    });

    [Benchmark]
    public void SqliteCommits() => RunWriters(writer =>
    {
        using SqliteCommand insert = _connections[writer].CreateCommand();
        insert.CommandText = "INSERT INTO events (id, writer) VALUES ($id, $writer)";
        SqliteParameter id = insert.Parameters.Add("$id", SqliteType.Integer);
        SqliteParameter owner = insert.Parameters.Add("$writer", SqliteType.Integer);
        owner.Value = writer;
        for (int i = 0; i < PerWriter; i++)
        {
            id.Value = writer * 1_000_000L + i;
            insert.ExecuteNonQuery();
        }
    });

    private int PerWriter => TotalCommits / Writers;

    /// <summary>Starts one thread per writer and waits for all of them. Dedicated threads keep pool scheduling out of the timing.</summary>
    private void RunWriters(Action<int> writer)
    {
        Thread[] threads = Enumerable.Range(0, Writers).Select(w => new Thread(() => writer(w))).ToArray();
        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }
    }

    private static SqliteConnection OpenSqlite(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using SqliteCommand setup = connection.CreateCommand();
        setup.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=60000;";
        setup.ExecuteNonQuery();
        using SqliteCommand create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE IF NOT EXISTS events (id INTEGER PRIMARY KEY, writer INTEGER NOT NULL)";
        create.ExecuteNonQuery();
        return connection;
    }
}
