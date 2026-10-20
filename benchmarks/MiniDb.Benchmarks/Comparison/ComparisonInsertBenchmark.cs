using BenchmarkDotNet.Attributes;
using LiteDB;
using LiteDocument = LiteDB.BsonDocument;
using MongoDocument = MongoDB.Bson.BsonDocument;

namespace MiniDb.Benchmarks.Comparison;

/// <summary>Batch load: all rows in one transaction (one InsertMany / InsertBulk call for document stores).</summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3, invocationCount: 1)]
public class ComparisonInsertBenchmark
{
    [Params(10_000)]
    public int Rows { get; set; }

    private EngineSet _engines = null!;

    [IterationSetup]
    public void Setup() => _engines = new EngineSet();

    [IterationCleanup]
    public void Cleanup() => _engines.Dispose();

    [Benchmark(Baseline = true)]
    public void MiniDbBatch() => BenchmarkData.LoadMiniDb(_engines.MiniDb, Rows);

    [Benchmark]
    public void SqliteBatch() => BenchmarkData.LoadSqlite(_engines.Sqlite, Rows);

    [Benchmark]
    public int LiteDbBatch() => _engines.LiteUsers.InsertBulk(LiteDocs());

    [Benchmark]
    public void MongoDbBatch() => _engines.MongoUsers.InsertMany(MongoDocs());

    private IEnumerable<LiteDocument> LiteDocs() => Enumerable.Range(0, Rows).Select(i => EngineSet.LiteDoc(i));

    private IEnumerable<MongoDocument> MongoDocs() => Enumerable.Range(0, Rows).Select(i => EngineSet.MongoDoc(i));
}
