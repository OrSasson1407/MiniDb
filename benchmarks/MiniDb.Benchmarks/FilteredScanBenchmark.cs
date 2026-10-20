using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using MiniDb.Api;

namespace MiniDb.Benchmarks;

/// <summary>Full scan with a filter on a non-key column. About a tenth of the rows match.</summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class FilteredScanBenchmark
{
    [Params(100_000)]
    public int Rows { get; set; }

    private string _directory = string.Empty;
    private MiniDatabase _db = null!;
    private Table _users = null!;
    private SqliteConnection _sqlite = null!;

    [GlobalSetup]
    public void Setup()
    {
        _directory = Directory.CreateTempSubdirectory("bench-filter-").FullName;
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
    public int MiniDbFilteredScanObjects() => _users.Scan(row => (int)row[2]! == 7).Count();

    [Benchmark]
    public int MiniDbFilteredScanViews() => _users.ScanViews(view => view.GetInt32(2) == 7).Count();

    [Benchmark]
    public int SqliteFilteredScan()
    {
        using SqliteCommand command = _sqlite.CreateCommand();
        command.CommandText = "SELECT id, name, score FROM users WHERE score = 7";
        using SqliteDataReader reader = command.ExecuteReader();
        int count = 0;
        while (reader.Read())
        {
            count++;
        }

        return count;
    }
}
