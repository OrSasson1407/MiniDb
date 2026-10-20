using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using MiniDb.Api;

namespace MiniDb.Benchmarks;

/// <summary>Many small transactions: every insert commits on its own. Measures the cost of each commit.</summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3, invocationCount: 1)]
public class AutocommitBenchmark
{
    private const int Inserts = 1000;

    [Params(true, false)]
    public bool FsyncOnCommit { get; set; }

    private string _directory = string.Empty;
    private MiniDatabase _db = null!;
    private Table _users = null!;
    private SqliteConnection _sqlite = null!;
    private SqliteCommand _insert = null!;

    [IterationSetup]
    public void Setup()
    {
        _directory = Directory.CreateTempSubdirectory("bench-autocommit-").FullName;
        _db = MiniDatabase.Open(Path.Combine(_directory, "bench.mdb"), new DatabaseOptions { FsyncOnCommit = FsyncOnCommit });
        _db.CreateTable(BenchmarkData.UsersSchema());
        _users = _db.GetTable("users");

        _sqlite = new SqliteConnection($"Data Source={Path.Combine(_directory, "bench.sqlite")}");
        _sqlite.Open();
        using (SqliteCommand pragma = _sqlite.CreateCommand())
        {
            pragma.CommandText = FsyncOnCommit ? "PRAGMA synchronous=FULL" : "PRAGMA synchronous=OFF";
            pragma.ExecuteNonQuery();
        }

        using (SqliteCommand create = _sqlite.CreateCommand())
        {
            create.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL, score INTEGER NOT NULL)";
            create.ExecuteNonQuery();
        }

        _insert = _sqlite.CreateCommand();
        _insert.CommandText = "INSERT INTO users (id, name, score) VALUES ($id, $name, $score)";
        _insert.Parameters.Add("$id", SqliteType.Integer);
        _insert.Parameters.Add("$name", SqliteType.Text);
        _insert.Parameters.Add("$score", SqliteType.Integer);
    }

    [IterationCleanup]
    public void Cleanup()
    {
        _insert.Dispose();
        _sqlite.Dispose();
        _db.Dispose();
        BenchmarkData.DeleteDirectory(_directory);
    }

    [Benchmark(Baseline = true)]
    public void MiniDbAutocommitInserts()
    {
        for (long id = 0; id < Inserts; id++)
        {
            _users.Insert(new object?[] { id, BenchmarkData.NameFor(id), BenchmarkData.ScoreFor(id) });
        }
    }

    [Benchmark]
    public void SqliteAutocommitInserts()
    {
        for (long id = 0; id < Inserts; id++)
        {
            _insert.Parameters["$id"].Value = id;
            _insert.Parameters["$name"].Value = BenchmarkData.NameFor(id);
            _insert.Parameters["$score"].Value = BenchmarkData.ScoreFor(id);
            _insert.ExecuteNonQuery();
        }
    }
}
