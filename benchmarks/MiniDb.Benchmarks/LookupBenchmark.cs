using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using MiniDb.Api;
using MiniDb.Records;

namespace MiniDb.Benchmarks;

/// <summary>Random primary-key lookups. Each invocation performs 1000 lookups.</summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class LookupBenchmark
{
    private const int LookupsPerInvoke = 1000;

    [Params(10_000, 100_000)]
    public int Rows { get; set; }

    private string _directory = string.Empty;
    private MiniDatabase _db = null!;
    private Table _users = null!;
    private SqliteConnection _sqlite = null!;
    private SqliteCommand _lookup = null!;
    private long[] _keys = Array.Empty<long>();

    [GlobalSetup]
    public void Setup()
    {
        _directory = Directory.CreateTempSubdirectory("bench-lookup-").FullName;
        _db = BenchmarkData.OpenFreshMiniDb(Path.Combine(_directory, "bench.mdb"));
        BenchmarkData.LoadMiniDb(_db, Rows);
        _users = _db.GetTable("users");

        _sqlite = BenchmarkData.OpenFreshSqlite(Path.Combine(_directory, "bench.sqlite"));
        BenchmarkData.LoadSqlite(_sqlite, Rows);
        _lookup = _sqlite.CreateCommand();
        _lookup.CommandText = "SELECT name FROM users WHERE id = $id";
        _lookup.Parameters.Add("$id", SqliteType.Integer);

        var random = new Random(7);
        _keys = Enumerable.Range(0, LookupsPerInvoke).Select(_ => random.NextInt64(0, Rows)).ToArray();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _lookup.Dispose();
        _sqlite.Dispose();
        _db.Dispose();
        BenchmarkData.DeleteDirectory(_directory);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = LookupsPerInvoke)]
    public object? MiniDbPointLookup()
    {
        object?[]? row = null;
        foreach (long key in _keys)
        {
            row = _users.Get(key);
        }

        return row;
    }

    [Benchmark(OperationsPerInvoke = LookupsPerInvoke)]
    public RowView? MiniDbPointLookupView()
    {
        RowView? view = null;
        foreach (long key in _keys)
        {
            view = _users.GetView(key);
        }

        return view;
    }

    [Benchmark(OperationsPerInvoke = LookupsPerInvoke)]
    public object? SqlitePointLookup()
    {
        object? name = null;
        foreach (long key in _keys)
        {
            _lookup.Parameters["$id"].Value = key;
            name = _lookup.ExecuteScalar();
        }

        return name;
    }
}
