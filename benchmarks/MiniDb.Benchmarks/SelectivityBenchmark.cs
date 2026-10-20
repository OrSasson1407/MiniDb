using BenchmarkDotNet.Attributes;
using MiniDb.Api;

namespace MiniDb.Benchmarks;

/// <summary>
/// The same filter on a table with and without an index on score, at three selectivities. Where the filter matches
/// many rows the planner should ignore the index, so the indexed table must not be slower than the plain one.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 5)]
public class SelectivityBenchmark
{
    private const int Rows = 100_000;

    [Params("score = 7", "score < 5", "score < 500")]
    public string Filter { get; set; } = string.Empty;

    private string _directory = string.Empty;
    private MiniDatabase _plain = null!;
    private MiniDatabase _indexed = null!;

    [GlobalSetup]
    public void Setup()
    {
        _directory = Directory.CreateTempSubdirectory("bench-selectivity-").FullName;
        _plain = BenchmarkData.OpenDefaultMiniDb(Path.Combine(_directory, "plain.mdb"));
        _indexed = BenchmarkData.OpenDefaultMiniDb(Path.Combine(_directory, "indexed.mdb"));
        BenchmarkData.LoadMiniDbChunked(_plain, Rows);
        BenchmarkData.LoadMiniDbChunked(_indexed, Rows);
        _indexed.Execute("CREATE INDEX ON users (score)");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _plain.Dispose();
        _indexed.Dispose();
        BenchmarkData.DeleteDirectory(_directory);
    }

    [Benchmark(Baseline = true)]
    public int WithoutIndex() => _plain.Execute($"SELECT id, name, score FROM users WHERE {Filter}").Rows.Count;

    [Benchmark]
    public int WithIndex() => _indexed.Execute($"SELECT id, name, score FROM users WHERE {Filter}").Rows.Count;
}
