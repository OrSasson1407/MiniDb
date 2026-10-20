using Microsoft.Data.Sqlite;
using MiniDb.Api;
using MiniDb.Records;
using MiniDb.Transactions;

namespace MiniDb.Benchmarks;

/// <summary>Shared setup: the same table in MiniDb and SQLite, loaded with the same rows.</summary>
internal static class BenchmarkData
{
    /// <summary>Large enough to hold a whole batch transaction, which the no-steal pool requires.</summary>
    private const int BatchPoolPages = 8192;

    public static TableSchema UsersSchema() => new(
        "users",
        new[]
        {
            new ColumnDefinition("id", ColumnType.Int64),
            new ColumnDefinition("name", ColumnType.String, maxLength: 40),
            new ColumnDefinition("score", ColumnType.Int32),
        },
        primaryKeyIndex: 0);

    public static MiniDatabase OpenFreshMiniDb(string path)
    {
        var db = MiniDatabase.Open(path, new DatabaseOptions { BufferPoolPages = BatchPoolPages });
        db.CreateTable(UsersSchema());
        return db;
    }

    public static SqliteConnection OpenFreshSqlite(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using SqliteCommand create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL, score INTEGER NOT NULL)";
        create.ExecuteNonQuery();
        return connection;
    }

    public static string NameFor(long id) => $"user-{id:D8}";

    /// <summary>Roughly one row in ten has score 7, so a filter on it returns a tenth of the table.</summary>
    public static int ScoreFor(long id) => (int)(id % 10);

    public static void LoadMiniDb(MiniDatabase db, int rows)
    {
        Table users = db.GetTable("users");
        using Transaction transaction = db.BeginTransaction();
        for (long id = 0; id < rows; id++)
        {
            users.Insert(new object?[] { id, NameFor(id), ScoreFor(id) });
        }

        transaction.Commit();
    }

    public static void LoadSqlite(SqliteConnection connection, int rows)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO users (id, name, score) VALUES ($id, $name, $score)";
        SqliteParameter id = insert.Parameters.Add("$id", SqliteType.Integer);
        SqliteParameter name = insert.Parameters.Add("$name", SqliteType.Text);
        SqliteParameter score = insert.Parameters.Add("$score", SqliteType.Integer);
        for (long key = 0; key < rows; key++)
        {
            id.Value = key;
            name.Value = NameFor(key);
            score.Value = ScoreFor(key);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Releases SQLite's pooled file handles so the directory can be deleted.</summary>
    public static void DeleteDirectory(string directory)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }
}
