using BenchmarkDotNet.Attributes;
using LiteDB;
using Microsoft.Data.Sqlite;
using MiniDb.Api;
using MiniDb.Records;
using MongoDB.Bson;
using MongoDB.Driver;
using LiteDocument = LiteDB.BsonDocument;
using MongoDocument = MongoDB.Bson.BsonDocument;

namespace MiniDb.Benchmarks.Comparison;

/// <summary>Point lookups by primary key. Each invocation performs 1000 lookups on a loaded table.</summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class ComparisonLookupBenchmark
{
    private const int LookupsPerInvoke = 1000;

    [Params(100_000)]
    public int Rows { get; set; }

    private EngineSet _engines = null!;
    private SqliteCommand _sqliteLookup = null!;
    private long[] _keys = Array.Empty<long>();

    [GlobalSetup]
    public void Setup()
    {
        _engines = new EngineSet();
        _engines.LoadAll(Rows);

        _sqliteLookup = _engines.Sqlite.CreateCommand();
        _sqliteLookup.CommandText = "SELECT name FROM users WHERE id = $id";
        _sqliteLookup.Parameters.Add("$id", SqliteType.Integer);

        var random = new Random(7);
        _keys = Enumerable.Range(0, LookupsPerInvoke).Select(_ => random.NextInt64(0, Rows)).ToArray();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _sqliteLookup.Dispose();
        _engines.Dispose();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = LookupsPerInvoke)]
    public object? MiniDbLookup()
    {
        RowView? view = null;
        foreach (long key in _keys)
        {
            view = _engines.Users.GetView(key);
        }

        return view;
    }

    [Benchmark(OperationsPerInvoke = LookupsPerInvoke)]
    public object? SqliteLookup()
    {
        using SqliteTransaction transaction = _engines.Sqlite.BeginTransaction();
        _sqliteLookup.Transaction = transaction;
        object? name = null;
        foreach (long key in _keys)
        {
            _sqliteLookup.Parameters["$id"].Value = key;
            name = _sqliteLookup.ExecuteScalar();
        }

        transaction.Commit();
        return name;
    }

    [Benchmark(OperationsPerInvoke = LookupsPerInvoke)]
    public LiteDocument? LiteDbLookup()
    {
        LiteDocument? document = null;
        foreach (long key in _keys)
        {
            document = _engines.LiteUsers.FindById(key);
        }

        return document;
    }

    [Benchmark(OperationsPerInvoke = LookupsPerInvoke)]
    public MongoDocument? MongoDbLookup()
    {
        MongoDocument? document = null;
        foreach (long key in _keys)
        {
            document = _engines.MongoUsers.Find(Builders<MongoDocument>.Filter.Eq("_id", key)).FirstOrDefault();
        }

        return document;
    }
}

/// <summary>Full scan and a filter on a non-key column. About a tenth of the rows match. No secondary indexes anywhere.</summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class ComparisonScanBenchmark
{
    [Params(100_000)]
    public int Rows { get; set; }

    private EngineSet _engines = null!;
    private SqliteCommand _sqliteFull = null!;
    private SqliteCommand _sqliteFiltered = null!;

    [GlobalSetup]
    public void Setup()
    {
        _engines = new EngineSet();
        _engines.LoadAll(Rows);

        _sqliteFull = _engines.Sqlite.CreateCommand();
        _sqliteFull.CommandText = "SELECT id, name, score FROM users";
        _sqliteFiltered = _engines.Sqlite.CreateCommand();
        _sqliteFiltered.CommandText = "SELECT id, name, score FROM users WHERE score = 7";
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _sqliteFull.Dispose();
        _sqliteFiltered.Dispose();
        _engines.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int MiniDbFullScan() => _engines.Users.ScanViews().Count();

    [Benchmark]
    public int SqliteFullScan() => CountReader(_sqliteFull);

    [Benchmark]
    public int LiteDbFullScan() => _engines.LiteUsers.FindAll().Count();

    [Benchmark]
    public int MongoDbFullScan() => CountMongo(Builders<MongoDocument>.Filter.Empty);

    [Benchmark]
    public int MiniDbFilteredScan() => _engines.MiniDb.Execute("SELECT id FROM users WHERE score = 7").Rows.Count;

    [Benchmark]
    public int SqliteFilteredScan() => CountReader(_sqliteFiltered);

    [Benchmark]
    public int LiteDbFilteredScan() => _engines.LiteUsers.Find(LiteDB.Query.EQ("score", 7)).Count();

    [Benchmark]
    public int MongoDbFilteredScan() => CountMongo(Builders<MongoDocument>.Filter.Eq("score", 7));

    private int CountReader(SqliteCommand command)
    {
        using SqliteTransaction transaction = _engines.Sqlite.BeginTransaction();
        command.Transaction = transaction;
        using SqliteDataReader reader = command.ExecuteReader();
        int count = 0;
        while (reader.Read())
        {
            count++;
        }

        transaction.Commit();
        return count;
    }

    private int CountMongo(FilterDefinition<MongoDocument> filter)
    {
        using IAsyncCursor<MongoDocument> cursor = _engines.MongoUsers.FindSync(filter);
        int count = 0;
        while (cursor.MoveNext())
        {
            count += cursor.Current.Count();
        }

        return count;
    }
}
