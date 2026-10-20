using BenchmarkDotNet.Attributes;
using MiniDb.Api;

namespace MiniDb.Benchmarks.Comparison;

/// <summary>Point lookups from several threads at once. Each invocation does 100,000 lookups per thread.</summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class ConcurrentLookupBenchmark
{
    private const int LookupsPerThread = 100_000;
    private const int Rows = 100_000;

    [Params(1, 2, 4, 8)]
    public int Threads { get; set; }

    private string _directory = string.Empty;
    private MiniDatabase _db = null!;
    private Table _users = null!;

    [GlobalSetup]
    public void Setup()
    {
        _directory = Directory.CreateTempSubdirectory("bench-concurrent-").FullName;
        _db = BenchmarkData.OpenDefaultMiniDb(Path.Combine(_directory, "bench.mdb"));
        BenchmarkData.LoadMiniDbChunked(_db, Rows);
        _users = _db.GetTable("users");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        BenchmarkData.DeleteDirectory(_directory);
    }

    [Benchmark]
    public void ParallelLookups()
    {
        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, thread =>
        {
            var random = new Random(thread);
            for (int i = 0; i < LookupsPerThread; i++)
            {
                _users.Get(random.NextInt64(0, Rows));
            }
        });
    }
}
