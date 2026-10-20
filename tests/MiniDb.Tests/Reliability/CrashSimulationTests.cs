using MiniDb.Api;
using MiniDb.Transactions;

namespace MiniDb.Tests.Reliability;

/// <summary>
/// In-process crash simulation. A random workload of transactions is cut off at random points: between commits, with a
/// transaction still open, or inside a commit whose log records are cut short. After every crash the database is reopened
/// (which runs recovery) and must show: every acknowledged commit present, every transaction all or nothing, and nothing
/// from a transaction that never committed.
///
/// The run is split into epochs. Each epoch starts a fresh, small database. After every crash the transactions of that
/// crash's iteration are checked one by one (a few point lookups, not a scan), and a full scan runs once per epoch. This keeps
/// one crash cheap enough for a million of them. MINIDB_CRASH_ITERATIONS sets the total.
///
/// A crash that leaves the files as they are is the same as a killed process: written bytes stay, and the buffer pool is
/// lost. Losing unsynced writes (power loss) is tested separately, in PowerLossTests.
/// </summary>
public class CrashSimulationTests : IDisposable
{
    private const int DefaultIterations = 40;

    /// <summary>Iterations per fresh database. Bounds the full scan done at the end of each epoch.</summary>
    private const int EpochLength = 1000;

    /// <summary>Upper bound for a transaction's row count. Lookups check this many keys to prove a transaction is absent.</summary>
    private const int MaxRows = 400;

    private readonly string _directory = Directory.CreateTempSubdirectory("minidb-crash-").FullName;

    /// <summary>Set per epoch. A small threshold checkpoints inside most commits; a large one leaves commits in the log to tear.</summary>
    private long _checkpointThreshold = 64 * 1024;

    private string DataPath => Path.Combine(_directory, "crash.mdb");

    private string WalPath => DataPath + ".wal";

    /// <summary>MINIDB_CRASH_ITERATIONS sets the total number of crashes (a million for the phase 1 criterion).</summary>
    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("MINIDB_CRASH_ITERATIONS"), out int value) ? value : DefaultIterations;

    /// <summary>MINIDB_CRASH_SEED lets several long runs cover different workloads.</summary>
    private static int Seed =>
        int.TryParse(Environment.GetEnvironmentVariable("MINIDB_CRASH_SEED"), out int value) ? value : 20261020;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void RandomCrashes_KeepEveryAcknowledgedCommit_AndNoPartialTransaction()
    {
        var random = new Random(Seed);
        int remaining = Iterations;
        bool smallThreshold = true;
        while (remaining > 0)
        {
            int epoch = Math.Min(EpochLength, remaining);
            _checkpointThreshold = smallThreshold ? 64 * 1024 : 1024 * 1024;
            RunEpoch(epoch, random);
            remaining -= epoch;
            smallThreshold = !smallThreshold;
        }
    }

    private void RunEpoch(int iterations, Random random)
    {
        DeleteFiles();
        MiniDatabase setup = Open();
        TransactionRows.CreateTable(setup);
        setup.SimulateCrash();

        var model = new TransactionModel();
        var touched = new List<long>();
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            MiniDatabase db = Open();
            CheckTouched(db, model, touched);

            for (int i = random.Next(1, 4); i > 0; i--)
            {
                long txn = model.Start();
                int rows = RandomRows(random);
                TransactionRows.InsertAndCommit(db, txn, rows);
                model.Acknowledge(txn, rows);
                touched.Add(txn);
            }

            switch (random.Next(3))
            {
                case 0:
                    CrashInsideCommit(db, model, random, touched);
                    break;
                case 1:
                    CrashWithOpenTransaction(db, model, random, touched);
                    break;
                default:
                    db.SimulateCrash();
                    break;
            }
        }

        MiniDatabase final = Open();
        TransactionModelCheck.Verify(final, model);
        final.Dispose();
    }

    /// <summary>
    /// The crash comes during a commit, before it is acknowledged. The log is cut at a random point between the bytes the
    /// commit added. Any cut point is valid: the transaction is then either fully present or fully absent.
    /// </summary>
    private void CrashInsideCommit(MiniDatabase db, TransactionModel model, Random random, List<long> touched)
    {
        long before = WalLength();
        long txn = model.Start();
        // Always several pages: only then can a cut log leave some pages of the transaction and not others.
        int rows = random.Next(100, MaxRows);
        TransactionRows.InsertAndCommit(db, txn, rows);
        model.Attempt(txn, rows);
        touched.Add(txn);
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
    private static void CrashWithOpenTransaction(MiniDatabase db, TransactionModel model, Random random, List<long> touched)
    {
        long txn = model.Start();
        int rows = RandomRows(random);
        Transaction open = TransactionRows.InsertOpen(db, txn, rows);
        model.MustBeAbsentTxn(txn);
        touched.Add(txn);
        db.SimulateCrash();
        GC.KeepAlive(open);
    }

    /// <summary>
    /// Checks the transactions of the previous iteration with point lookups, and settles the ones that were in doubt.
    /// Acknowledged rows must be complete, in-doubt ones all or nothing, and absent ones must be absent.
    /// </summary>
    private static void CheckTouched(MiniDatabase db, TransactionModel model, List<long> touched)
    {
        Table users = db.GetTable("users");
        var counts = new Dictionary<long, int>();
        foreach (long txn in touched)
        {
            if (model.Acknowledged.TryGetValue(txn, out int rows))
            {
                // Only the keys the transaction wrote: an extra row would be caught by the scan at the end of the epoch.
                Assert.True(CountPresent(users, txn, rows) == rows,
                    $"acknowledged transaction {txn} is not complete after recovery");
            }
            else if (model.InDoubt.TryGetValue(txn, out int doubt))
            {
                int present = CountPresent(users, txn, doubt);
                Assert.True(present == 0 || present == doubt, $"transaction {txn} is partly visible: {present} of {doubt} rows");
                counts[txn] = present;
            }
            else
            {
                // Never committed: every key it could have used must be absent.
                Assert.True(CountPresent(users, txn, MaxRows) == 0, $"transaction {txn} never committed, yet its rows are visible");
            }
        }

        model.Settle(counts);
        touched.Clear();
    }

    private static int CountPresent(Table users, long txn, int keys)
    {
        int present = 0;
        for (int j = 0; j < keys; j++)
        {
            if (users.Get(txn * TransactionRows.KeySpace + j) is not null)
            {
                present++;
            }
        }

        return present;
    }

    /// <summary>Most transactions are small; some span several pages, which is what lets a cut log tear one in half.</summary>
    private static int RandomRows(Random random) => random.Next(10) == 0 ? random.Next(100, MaxRows) : random.Next(1, 20);

    private void DeleteFiles()
    {
        File.Delete(DataPath);
        File.Delete(WalPath);
    }

    private MiniDatabase Open() => MiniDatabase.Open(DataPath, new DatabaseOptions
    {
        BufferPoolPages = 64,
        CheckpointThresholdBytes = _checkpointThreshold,
    });

    private long WalLength() => File.Exists(WalPath) ? new FileInfo(WalPath).Length : 0;
}
