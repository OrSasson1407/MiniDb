using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using MiniDb.Api;

namespace MiniDb.Benchmarks;

/// <summary>The same filtered query through each engine's SQL front end. About a tenth of the rows match.</summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class SqlFilteredScanBenchmark
{
    [Params(100_000)]
    public int Rows { get; set; }

    private string _directory = string.Empty;
    private MiniDatabase _db = null!;
    private SqliteConnection _sqlite = null!;

    [GlobalSetup]
    public void Setup()
    {
        _directory = Directory.CreateTempSubdirectory("bench-sqlfilter-").FullName;
        _db = BenchmarkData.OpenFreshMiniDb(Path.Combine(_directory, "bench.mdb"));
        BenchmarkData.LoadMiniDb(_db, Rows);

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
    public int MiniDbSqlFilteredScan() => _db.Execute("SELECT id FROM users WHERE score = 7").Rows.Count;

    [Benchmark]
    public int SqliteSqlFilteredScan()
    {
        using SqliteCommand command = _sqlite.CreateCommand();
        command.CommandText = "SELECT id FROM users WHERE score = 7";
        using SqliteDataReader reader = command.ExecuteReader();
        int count = 0;
        while (reader.Read())
        {
            count++;
        }

        return count;
    }
}
