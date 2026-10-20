using MiniDb.Api;
using MiniDb.Transactions;

namespace MiniDb.Tests.Reliability;

/// <summary>
/// Rows written by numbered transactions. A row's key is transaction * KeySpace + index, and its name carries the transaction,
/// so every row says which transaction wrote it. Shared by the crash, fault and power-loss tests.
/// </summary>
internal static class TransactionRows
{
    /// <summary>Wider than the largest transaction, so keys never collide.</summary>
    public const int KeySpace = 512;

    /// <summary>Padded so that a transaction of a few hundred rows spans several pages.</summary>
    public static string Name(long txn) => $"t{txn}:" + new string('x', 35);

    public static void CreateTable(MiniDatabase db) =>
        db.Execute("CREATE TABLE users (id BIGINT PRIMARY KEY, name VARCHAR(60))");

    /// <summary>Inserts the rows and commits. Throws if the commit fails.</summary>
    public static void InsertAndCommit(MiniDatabase db, long txn, int rows)
    {
        using Transaction transaction = db.BeginTransaction();
        Insert(db, txn, rows);
        transaction.Commit();
    }

    /// <summary>Inserts the rows into a transaction the caller keeps open and does not commit.</summary>
    public static Transaction InsertOpen(MiniDatabase db, long txn, int rows)
    {
        Transaction transaction = db.BeginTransaction();
        Insert(db, txn, rows);
        return transaction;
    }

    private static void Insert(MiniDatabase db, long txn, int rows)
    {
        Table users = db.GetTable("users");
        for (int j = 0; j < rows; j++)
        {
            users.Insert(new object?[] { txn * KeySpace + j, Name(txn) });
        }
    }
}

/// <summary>What a test knows about every transaction it has started, and what a crash may have done to it.</summary>
internal sealed class TransactionModel
{
    private long _next = 1;
    private readonly Dictionary<long, int> _inDoubt = new();
    private readonly HashSet<long> _absent = new();

    /// <summary>Transactions whose commit returned, with their row counts.</summary>
    public Dictionary<long, int> Acknowledged { get; } = new();

    /// <summary>Transactions whose commit was interrupted: all of their rows or none may be visible.</summary>
    public IReadOnlyDictionary<long, int> InDoubt => _inDoubt;

    /// <summary>Transactions that never committed: none of their rows may be visible.</summary>
    public IReadOnlySet<long> MustBeAbsent => _absent;

    public long Start() => _next++;

    public bool IsKnown(long txn) => txn < _next;

    public void Acknowledge(long txn, int rows) => Acknowledged[txn] = rows;

    public void Attempt(long txn, int rows) => _inDoubt[txn] = rows;

    public void MustBeAbsentTxn(long txn) => _absent.Add(txn);

    /// <summary>After verification, an in-doubt transaction is either committed for good or gone for good.</summary>
    public void Settle(Dictionary<long, int> counts)
    {
        foreach ((long txn, int rows) in _inDoubt.ToList())
        {
            if (counts.GetValueOrDefault(txn) == rows)
            {
                Acknowledged[txn] = rows;
            }
        }

        _inDoubt.Clear();
        _absent.Clear();
    }
}

internal static class TransactionModelCheck
{
    /// <summary>
    /// Reads every row and checks it against the model: acknowledged transactions are complete, in-doubt transactions are
    /// all or nothing, and no row belongs to a transaction that never started or never committed. Then settles the model.
    /// </summary>
    public static void Verify(MiniDatabase db, TransactionModel model)
    {
        var counts = new Dictionary<long, int>();
        foreach (object?[] row in db.GetTable("users").Scan())
        {
            long id = (long)row[0]!;
            long txn = id / TransactionRows.KeySpace;
            Assert.Equal(TransactionRows.Name(txn), row[1]);
            Assert.True(model.IsKnown(txn), $"row of transaction {txn} is visible, but that transaction was never started");
            counts[txn] = counts.GetValueOrDefault(txn) + 1;
        }

        foreach ((long txn, int rows) in model.Acknowledged)
        {
            Assert.True(counts.GetValueOrDefault(txn) == rows,
                $"acknowledged transaction {txn} has {counts.GetValueOrDefault(txn)} of {rows} rows after recovery");
        }

        foreach ((long txn, int rows) in model.InDoubt)
        {
            int present = counts.GetValueOrDefault(txn);
            Assert.True(present == 0 || present == rows,
                $"transaction {txn} is partly visible: {present} of {rows} rows");
        }

        foreach (long txn in model.MustBeAbsent)
        {
            Assert.True(counts.GetValueOrDefault(txn) == 0, $"transaction {txn} never committed, yet its rows are visible");
        }

        model.Settle(counts);
    }
}
