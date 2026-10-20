using LiteDB;
using Microsoft.Data.Sqlite;
using MiniDb.Api;
using MongoDB.Bson;
using MongoDB.Driver;
using LiteDocument = LiteDB.BsonDocument;
using MongoDocument = MongoDB.Bson.BsonDocument;

namespace MiniDb.Benchmarks.Comparison;

/// <summary>One empty store per engine. Each holds the same users table: id, name, score.</summary>
internal sealed class EngineSet : IDisposable
{
    private const string MongoDatabaseName = "minidb_bench";
    private static readonly MongoClient MongoClient = new("mongodb://localhost:27017");

    private readonly string _mongoCollectionName = "users_" + Guid.NewGuid().ToString("N");

    public EngineSet()
    {
        Directory = System.IO.Directory.CreateTempSubdirectory("bench-cmp-").FullName;

        MiniDb = BenchmarkData.OpenFreshMiniDb(Path.Combine(Directory, "bench.mdb"));
        Users = MiniDb.GetTable("users");

        Sqlite = BenchmarkData.OpenFreshSqlite(Path.Combine(Directory, "bench.sqlite"));

        LiteDb = new LiteDatabase(Path.Combine(Directory, "bench.litedb"));
        LiteUsers = LiteDb.GetCollection<LiteDocument>("users");

        MongoDb = MongoClient.GetDatabase(MongoDatabaseName);
        MongoUsers = MongoDb.GetCollection<MongoDocument>(_mongoCollectionName);
    }

    public string Directory { get; }

    public MiniDatabase MiniDb { get; }

    public MiniDb.Api.Table Users { get; }

    public SqliteConnection Sqlite { get; }

    public LiteDatabase LiteDb { get; }

    public ILiteCollection<LiteDocument> LiteUsers { get; }

    public IMongoDatabase MongoDb { get; }

    public IMongoCollection<MongoDocument> MongoUsers { get; }

    public static LiteDocument LiteDoc(long id) => new()
    {
        ["_id"] = id,
        ["name"] = BenchmarkData.NameFor(id),
        ["score"] = BenchmarkData.ScoreFor(id),
    };

    public static MongoDocument MongoDoc(long id) => new()
    {
        ["_id"] = id,
        ["name"] = BenchmarkData.NameFor(id),
        ["score"] = BenchmarkData.ScoreFor(id),
    };

    /// <summary>Loads the same rows into every engine.</summary>
    public void LoadAll(int rows)
    {
        BenchmarkData.LoadMiniDb(MiniDb, rows);
        BenchmarkData.LoadSqlite(Sqlite, rows);
        LiteUsers.InsertBulk(Enumerable.Range(0, rows).Select(i => LiteDoc(i)));
        MongoUsers.InsertMany(Enumerable.Range(0, rows).Select(i => MongoDoc(i)));
    }

    public void Dispose()
    {
        Sqlite.Dispose();
        MiniDb.Dispose();
        LiteDb.Dispose();
        try
        {
            MongoDb.DropCollection(_mongoCollectionName);
        }
        finally
        {
            BenchmarkData.DeleteDirectory(Directory);
        }
    }
}
