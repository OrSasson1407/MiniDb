using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using MiniDb.Api;
using LiteDocument = LiteDB.BsonDocument;
using MongoDocument = MongoDB.Bson.BsonDocument;

namespace MiniDb.Benchmarks.Comparison;

/// <summary>Each insert is its own transaction with default durability. Measures per-commit cost.</summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3, invocationCount: 1)]
public class ComparisonAutocommitBenchmark
{
    [Params(1_000)]
    public int Inserts { get; set; }

    private EngineSet _engines = null!;
    private SqliteCommand _sqliteInsert = null!;

    [IterationSetup]
    public void Setup()
    {
        _engines = new EngineSet();
        _sqliteInsert = _engines.Sqlite.CreateCommand();
        _sqliteInsert.CommandText = "INSERT INTO users (id, name, score) VALUES ($id, $name, $score)";
        _sqliteInsert.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
        _sqliteInsert.Parameters.Add("$name", Microsoft.Data.Sqlite.SqliteType.Text);
        _sqliteInsert.Parameters.Add("$score", Microsoft.Data.Sqlite.SqliteType.Integer);
    }

    [IterationCleanup]
    public void Cleanup()
    {
        _sqliteInsert.Dispose();
        _engines.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void MiniDbAutocommit()
    {
        for (long id = 0; id < Inserts; id++)
        {
            _engines.Users.Insert(new object?[] { id, BenchmarkData.NameFor(id), BenchmarkData.ScoreFor(id) });
        }
    }

    [Benchmark]
    public void SqliteAutocommit()
    {
        for (long id = 0; id < Inserts; id++)
        {
            _sqliteInsert.Parameters["$id"].Value = id;
            _sqliteInsert.Parameters["$name"].Value = BenchmarkData.NameFor(id);
            _sqliteInsert.Parameters["$score"].Value = BenchmarkData.ScoreFor(id);
            _sqliteInsert.ExecuteNonQuery();
        }
    }

    [Benchmark]
    public void LiteDbAutocommit()
    {
        for (long id = 0; id < Inserts; id++)
        {
            _engines.LiteUsers.Insert(EngineSet.LiteDoc(id));
        }
    }

    [Benchmark]
    public void MongoDbAutocommit()
    {
        for (long id = 0; id < Inserts; id++)
        {
            _engines.MongoUsers.InsertOne(EngineSet.MongoDoc(id));
        }
    }
}
