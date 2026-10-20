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
        // Dedicated threads: the thread pool's start-up and scheduling would otherwise be part of the measurement.
        Thread[] workers = Enumerable.Range(0, Threads).Select(thread => new Thread(() => LookupLoop(thread))).ToArray();
        foreach (Thread worker in workers)
        {
            worker.Start();
        }

        foreach (Thread worker in workers)
        {
            worker.Join();
        }
    }

    private void LookupLoop(int seed)
    {
        var random = new Random(seed);
        for (int i = 0; i < LookupsPerThread; i++)
        {
            _users.Get(random.NextInt64(0, Rows));
        }
    }
}
