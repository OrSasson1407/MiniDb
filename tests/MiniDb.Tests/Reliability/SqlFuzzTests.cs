using MiniDb.Api;
using MiniDb.Query;

namespace MiniDb.Tests.Reliability;

/// <summary>
/// Random SQL must never cause anything except a <see cref="MiniDbException"/>. A statement may be rejected or fail,
/// but a crash of the parser, the planner or the executor is a bug. After the fuzzing, the table must still read back.
/// </summary>
public class SqlFuzzTests : IDisposable
{
    private static readonly string[] Words =
    {
        "SELECT", "FROM", "WHERE", "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "AND", "OR", "ORDER", "BY",
        "GROUP", "JOIN", "ON", "INNER", "LIMIT", "DESC", "ASC", "COUNT", "SUM", "AVG", "MIN", "MAX", "CREATE", "TABLE",
        "INDEX", "PRIMARY", "KEY", "INT", "BIGINT", "DOUBLE", "BOOL", "VARCHAR", "TRUE", "FALSE", "users", "orders",
        "id", "name", "score", "users.id", "orders.user_id", "*", "(", ")", ",", "=", "<", ">", "<=", ">=", ".", "'x'",
        "''", "'", "'unterminated", "1", "0", "-5", "2.5", "9999999999999999999999", "-", "@", "?", ";", "--", "\u00e9",
        "\u0000", "\uffff", "\U0001F600",
    };

    private readonly string _directory = Directory.CreateTempSubdirectory("minidb-sqlfuzz-").FullName;

    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("MINIDB_FUZZ_ITERATIONS"), out int value) ? value : 3000;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void RandomTokenSequences_OnlyEverFailWithEngineErrors()
    {
        using MiniDatabase db = Open();
        Seed(db);
        var random = new Random(7);

        for (int i = 0; i < Iterations; i++)
        {
            string sql = RandomStatement(random);
            RunExpectingEngineErrorsOnly(db, sql);
        }

        AssertTableStillReadsBack(db);
    }

    [Fact]
    public void RandomBytes_OnlyEverFailWithEngineErrors()
    {
        using MiniDatabase db = Open();
        Seed(db);
        var random = new Random(11);

        for (int i = 0; i < Iterations / 4; i++)
        {
            var chars = new char[random.Next(0, 80)];
            for (int c = 0; c < chars.Length; c++)
            {
                chars[c] = (char)random.Next(0, 0x10000);
            }

            RunExpectingEngineErrorsOnly(db, new string(chars));
        }

        AssertTableStillReadsBack(db);
    }

    private static void RunExpectingEngineErrorsOnly(MiniDatabase db, string sql)
    {
        try
        {
            db.Execute(sql);
        }
        catch (MiniDbException)
        {
            // An expected rejection: the statement is invalid, unsupported, or does not fit the data.
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException($"Engine crashed with {ex.GetType().Name}: {ex.Message}\nSQL: {Escape(sql)}");
        }
    }

    private static string RandomStatement(Random random)
    {
        int length = random.Next(1, 14);
        var parts = new string[length];
        for (int i = 0; i < length; i++)
        {
            parts[i] = Words[random.Next(Words.Length)];
        }

        return string.Join(random.Next(3) == 0 ? "" : " ", parts);
    }

    private static void Seed(MiniDatabase db)
    {
        db.Execute("CREATE TABLE users (id BIGINT PRIMARY KEY, name VARCHAR(20), score INT)");
        db.Execute("CREATE TABLE orders (id BIGINT PRIMARY KEY, user_id BIGINT, total DOUBLE)");
        for (int i = 1; i <= 20; i++)
        {
            db.Execute($"INSERT INTO users VALUES ({i}, 'u{i}', {i % 5})");
            db.Execute($"INSERT INTO orders VALUES ({i}, {i % 7}, {i * 1.5})");
        }
    }

    /// <summary>Valid random DELETE statements may remove rows, so only the readability of the table is checked.</summary>
    private static void AssertTableStillReadsBack(MiniDatabase db)
    {
        QueryResult result = db.Execute("SELECT id FROM users");
        Assert.Equal(new[] { "id" }, result.Columns);
    }

    private MiniDatabase Open() => MiniDatabase.Open(Path.Combine(_directory, "fuzz.mdb"));

    private static string Escape(string text) => text.Replace("\u0000", "\\0").Replace("\n", "\\n");
}
