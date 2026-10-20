using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using MiniDb.Api;

namespace MiniDb.Benchmarks;

/// <summary>Reads every row of the table. Both sides decode every row.</summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class ScanBenchmark
{
    [Params(10_000, 100_000)]
    public int Rows { get; set; }

    private string _directory = string.Empty;
    private MiniDatabase _db = null!;
    private Table _users = null!;
    private SqliteConnection _sqlite = null!;

    [GlobalSetup]
    public void Setup()
    {
        _directory = Directory.CreateTempSubdirectory("bench-scan-").FullName;
        _db = BenchmarkData.OpenFreshMiniDb(Path.Combine(_directory, "bench.mdb"));
        BenchmarkData.LoadMiniDb(_db, Rows);
        _users = _db.GetTable("users");

        _sqlite = BenchmarkData.OpenFreshSqlite(Path.Combine(_directory, "bench.sqlite"));
        BenchmarkData.LoadSqlite(_sqlite, Rows);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _sqlite.Dispose();
        _db.Dispose();
        BenchmarkData.DeleteDirectory(_directory);
    }

    [Benchmark(Baseline = true)]
    public int MiniDbFullScan() => _users.Scan().Count();

    [Benchmark]
    public int SqliteFullScan()
    {
        using SqliteCommand command = _sqlite.CreateCommand();
        command.CommandText = "SELECT id, name FROM users";
        using SqliteDataReader reader = command.ExecuteReader();
        int count = 0;
        while (reader.Read())
        {
            count++;
        }

        return count;
    }
}
