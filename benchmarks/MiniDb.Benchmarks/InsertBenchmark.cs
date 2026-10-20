using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using MiniDb.Api;

namespace MiniDb.Benchmarks;

/// <summary>Loads N rows into a fresh database, all in one transaction, with MiniDb and SQLite.</summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3, invocationCount: 1)]
public class InsertBenchmark
{
    [Params(10_000)]
    public int Rows { get; set; }

    private string _directory = string.Empty;
    private MiniDatabase _db = null!;
    private SqliteConnection _sqlite = null!;

    [IterationSetup]
    public void Setup()
    {
        _directory = Directory.CreateTempSubdirectory("bench-insert-").FullName;
        _db = BenchmarkData.OpenFreshMiniDb(Path.Combine(_directory, "bench.mdb"));
        _sqlite = BenchmarkData.OpenFreshSqlite(Path.Combine(_directory, "bench.sqlite"));
    }

    [IterationCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        _sqlite.Dispose();
        BenchmarkData.DeleteDirectory(_directory);
    }

    [Benchmark(Baseline = true)]
    public void MiniDbInsertInOneTransaction() => BenchmarkData.LoadMiniDb(_db, Rows);

    [Benchmark]
    public void SqliteInsertInOneTransaction() => BenchmarkData.LoadSqlite(_sqlite, Rows);
}
