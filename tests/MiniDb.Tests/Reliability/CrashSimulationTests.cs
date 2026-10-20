using MiniDb.Api;
using MiniDb.Transactions;

namespace MiniDb.Tests.Reliability;

/// <summary>
/// In-process crash simulation. A random workload of transactions is cut off at random points: between commits, with a
/// transaction still open, or inside a commit whose log records are cut short. After every crash the database is reopened
/// (which runs recovery) and must show three things: every acknowledged commit is present, every transaction is all or
/// nothing, and nothing from a transaction that never committed is visible.
///
/// A crash that leaves the files as they are is the same as a killed process: written bytes stay, and the buffer pool is
/// lost. Power loss is not simulated here; that needs the operating system's cache to be cut, which a test cannot do.
/// </summary>
public class CrashSimulationTests : IDisposable
{
    /// <summary>Row key = transaction * KeySpace + row index, so the transaction can be read back from the key.</summary>
    private const int KeySpace = 512;

    private const int DefaultIterations = 40;

    private readonly string _directory = Directory.CreateTempSubdirectory("minidb-crash-").FullName;

    private string DataPath => Path.Combine(_directory, "crash.mdb");

    private string WalPath => DataPath + ".wal";

    /// <summary>MINIDB_CRASH_ITERATIONS raises the number of crashes for a long run (nightly job).</summary>
    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("MINIDB_CRASH_ITERATIONS"), out int value) ? value : DefaultIterations;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void RandomCrashes_KeepEveryAcknowledgedCommit_AndNoPartialTransaction()
    {
        var random = new Random(20261020);
        var model = new Model();

        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            MiniDatabase db = Open();
            if (iteration == 0)
            {
                db.Execute("CREATE TABLE users (id BIGINT PRIMARY KEY, name VARCHAR(60))");
            }

            Verify(db, model);

            for (int i = random.Next(1, 6); i > 0; i--)
            {
                long txn = model.Start();
                int rows = random.Next(1, 400);
                InsertTransaction(db, txn, rows);
                model.Acknowledge(txn, rows);
            }

            switch (random.Next(3))
            {
                case 0:
                    CrashInsideCommit(db, model, random);
                    break;
                case 1:
                    CrashWithOpenTransaction(db, model, random);
                    break;
                default:
                    db.SimulateCrash();
                    break;
            }
        }

        MiniDatabase final = Open();
        Verify(final, model);
        final.Dispose();
    }

    /// <summary>
    /// The crash comes during a commit, before it is acknowledged. The log is cut at a random point between the bytes
    /// the commit added. Any cut point is valid: the transaction is then either fully present or fully absent.
    /// </summary>
    private void CrashInsideCommit(MiniDatabase db, Model model, Random random)
    {
        long before = WalLength();
        long txn = model.Start();
        int rows = random.Next(1, 400);
        InsertTransaction(db, txn, rows);
        model.Attempt(txn, rows);
        db.SimulateCrash();

        long after = WalLength();
        if (after < before)
        {
            return; // a checkpoint ran inside the commit and emptied the log; the data file holds the transaction
        }

        long cut = random.NextInt64(before, after + 1);
        using var log = new FileStream(WalPath, FileMode.Open, FileAccess.ReadWrite);
        log.SetLength(cut);
    }

    /// <summary>The crash comes while a transaction is open and has written rows. Those rows must not survive.</summary>
    private void CrashWithOpenTransaction(MiniDatabase db, Model model, Random random)
    {
        long txn = model.Start();
        int rows = random.Next(1, 400);
        Table users = db.GetTable("users");
        Transaction open = db.BeginTransaction();
        for (int j = 0; j < rows; j++)
        {
            users.Insert(new object?[] { txn * KeySpace + j, Name(txn) });
        }

        model.MustBeAbsent(txn);
        db.SimulateCrash();
        GC.KeepAlive(open);
    }

    /// <summary>
    /// The name of a transaction's rows. It is padded so that a transaction of a few hundred rows spans several pages,
    /// which is what lets a cut log leave some pages of a transaction behind and not others.
    /// </summary>
    private static string Name(long txn) => $"t{txn}:" + new string('x', 35);

    private MiniDatabase Open() => MiniDatabase.Open(DataPath, new DatabaseOptions
    {
        BufferPoolPages = 64,
        CheckpointThresholdBytes = 64 * 1024,
    });

    private static void InsertTransaction(MiniDatabase db, long txn, int rows)
    {
        Table users = db.GetTable("users");
        using Transaction transaction = db.BeginTransaction();
        for (int j = 0; j < rows; j++)
        {
            users.Insert(new object?[] { txn * KeySpace + j, Name(txn) });
        }

        transaction.Commit();
    }

    /// <summary>
    /// Reads every row after a reopen and checks it against the model. Transactions that were in doubt at the crash are
    /// settled here: whatever survived becomes acknowledged, and whatever did not is forgotten.
    /// </summary>
    private static void Verify(MiniDatabase db, Model model)
    {
        var counts = new Dictionary<long, int>();
        foreach (object?[] row in db.GetTable("users").Scan())
        {
            long id = (long)row[0]!;
            long txn = id / KeySpace;
            Assert.StartsWith(Name(txn), (string)row[1]!);
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

        foreach (long txn in model.MustBeAbsentSet)
        {
            Assert.True(counts.GetValueOrDefault(txn) == 0, $"transaction {txn} never committed, yet its rows are visible");
        }

        model.Settle(counts);
    }

    private long WalLength() => File.Exists(WalPath) ? new FileInfo(WalPath).Length : 0;

    /// <summary>What the test knows about every transaction it has started.</summary>
    private sealed class Model
    {
        private long _next = 1;
        private readonly Dictionary<long, int> _inDoubt = new();
        private readonly HashSet<long> _absent = new();

        /// <summary>Transactions whose commit returned, with their row counts.</summary>
        public Dictionary<long, int> Acknowledged { get; } = new();

        /// <summary>Transactions whose commit was interrupted by a crash: all of their rows or none may be visible.</summary>
        public IReadOnlyDictionary<long, int> InDoubt => _inDoubt;

        /// <summary>Transactions that never committed: none of their rows may be visible.</summary>
        public IReadOnlySet<long> MustBeAbsentSet => _absent;

        public long Start() => _next++;

        public bool IsKnown(long txn) => txn < _next;

        public void Acknowledge(long txn, int rows) => Acknowledged[txn] = rows;

        public void Attempt(long txn, int rows) => _inDoubt[txn] = rows;

        public void MustBeAbsent(long txn) => _absent.Add(txn);

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
}
